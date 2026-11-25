using CodeAssist.Tools;
using System.Security.Cryptography;
using System.Text.Json;
using CodeAssist.Mcp;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Configuration;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace CodeAssist.Server.Composition;

public static class ConnectorComposition
{
    public static McpConnectionManager Create(CodeAssistSettings settings, string workspace, IReadOnlyDictionary<string, string> environment,
        Func<string, ConnectorOAuthSettings, ToolContext, ClientOAuthOptions?>? oauth = null,
        Func<string, ToolContext, McpClientOptions>? clientOptions = null,
        McpHostServices? host = null)
    {
        environment = new Dictionary<string, string>(environment, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var profiles = settings.Connectors.Servers.Where(pair => pair.Value.Enabled && !settings.Policy.Rules.Any(rule =>
            rule.Action.Equals("connector.use", StringComparison.OrdinalIgnoreCase) && rule.Effect != RuleDecision.Allow && new GlobMatcher([rule.Resource]).IsMatch(pair.Key))).Select(pair =>
        {
            var name = pair.Key;
            var definition = pair.Value;
            var command = definition.Command;
            var arguments = definition.Arguments.ToArray();
            var directory = Path.GetFullPath(definition.WorkingDirectory ?? workspace, workspace);
            var headers = definition.Headers.ToDictionary(item => item.Key, item => Expand(item.Value), StringComparer.OrdinalIgnoreCase);
            var childEnvironment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var item in definition.Environment) childEnvironment[item.Key] = Expand(item.Value);
            var endpoint = definition.Transport == ConnectorTransport.Http ? new Uri(definition.Url ?? throw new ArgumentException("HTTP MCP server requires an endpoint."), UriKind.Absolute) : null;
            if (endpoint is not null && (endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.Query.Length != 0 ||
                endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)))
                throw new ArgumentException("MCP endpoint must use HTTPS or loopback HTTP without credentials, fragments or query secrets.");
            if (endpoint is null && string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Stdio MCP server requires an executable.");
            var startup = TimeSpan.FromSeconds(definition.StartupTimeoutSeconds ?? settings.Connectors.StartupTimeoutSeconds);
            var catalog = TimeSpan.FromSeconds(definition.CatalogTimeoutSeconds ?? settings.Connectors.CatalogTimeoutSeconds);
            var call = TimeSpan.FromSeconds(definition.CallTimeoutSeconds ?? settings.Connectors.CallTimeoutSeconds);
            var mode = definition.HttpMode switch { ConnectorHttpMode.Streaming => HttpTransportMode.StreamableHttp, ConnectorHttpMode.Events => HttpTransportMode.Sse, _ => HttpTransportMode.AutoDetect };
            var frozen = definition.FreezeCatalog;
            var allowPrivate = settings.Connectors.AllowPrivateHosts;
            var revision = definition.ProtocolRevision;
            var authentication = new ConnectorOAuthSettings
            {
                Enabled = definition.Oauth.Enabled, ClientId = definition.Oauth.ClientId, Scope = definition.Oauth.Scope,
                Authority = definition.Oauth.Authority, CallbackPort = definition.Oauth.CallbackPort,
                ClientSecret = definition.Oauth.ClientSecret, ClientMetadataUri = definition.Oauth.ClientMetadataUri, RedirectUri = definition.Oauth.RedirectUri,
            };
            string Identity(ToolContext context)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Workspace = Path.GetFullPath(context.Workspace), Server = name, Endpoint = endpoint?.AbsoluteUri,
                    Headers = headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
                    authentication.ClientId, authentication.Scope, authentication.Authority, authentication.ClientMetadataUri, authentication.RedirectUri, authentication.CallbackPort,
                    ClientSecret = authentication.ClientSecret is { } secret ? Expand(secret) : null,
                });
                try { return Convert.ToHexString(SHA256.HashData(payload)); }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }
            return new McpServerProfile(name, async (context, cancellationToken) =>
            {
                var options = clientOptions?.Invoke(name, context) ?? new McpClientOptions();
                if (revision is not null) options.ProtocolVersion = revision;
                var budgets = new McpConnectionOptions { StartupTimeout = startup, CatalogTimeout = catalog, CallTimeout = call, Client = options, RefreshOnNotification = !frozen };
                if (endpoint is null)
                {
                    var transport = new StdioClientTransport(new()
                    {
                        Name = name, Command = command!, Arguments = arguments, WorkingDirectory = directory,
                        InheritEnvironmentVariables = false, EnvironmentVariables = childEnvironment,
                    });
                    return new McpConnectionLease(await McpToolConnection.ConnectAsync(name, transport, budgets, cancellationToken));
                }
                var http = new HttpClient(NetworkGuard.CreateHandler(endpoint.IsLoopback || allowPrivate)) { Timeout = Timeout.InfiniteTimeSpan };
                try
                {
                    var authorization = authentication.Enabled ? oauth?.Invoke(name, authentication, context) ?? CreateOAuth(Identity(context), authentication, context) : null;
                    if (authorization is not null && authorization.AuthorizationCallbackHandler is null) throw new ArgumentException("MCP OAuth requires a trusted state-aware callback.");
                    var transport = new HttpClientTransport(new() { Name = name, Endpoint = endpoint, AdditionalHeaders = headers, TransportMode = mode, OAuth = authorization }, http);
                    return new McpConnectionLease(await McpToolConnection.ConnectAsync(name, transport, budgets, cancellationToken), http);
                }
                catch { http.Dispose(); throw; }
            })
            {
                Allow = definition.Tools.Allow.ToArray(), Deny = definition.Tools.Deny.ToArray(), FreezeCatalog = frozen,
                ToolsEnabled = definition.Tools.DefaultEnabled, ToolOverrides = new Dictionary<string, bool>(definition.Tools.Entries, StringComparer.Ordinal),
                StartupEffects = endpoint is null ? [new(ToolEffectKind.Exec, $"mcp-process:{name}")] : [new(ToolEffectKind.Net, endpoint.GetLeftPart(UriPartial.Authority))],
                ForgetCredentials = host?.TokenCache is null ? null : (context, token) => host.ClearCredentials?.Invoke(Identity(context), context, token) ?? throw new NotSupportedException("Persistent MCP credential removal requires a trusted host callback."),
            };
        }).ToArray();
        return new(profiles);

        ClientOAuthOptions? CreateOAuth(string server, ConnectorOAuthSettings definition, ToolContext context)
        {
            if (host is null || host.Authorize is null && host.OpenBrowser is null) return null;
            if (definition.ClientSecret is not null && !EnvironmentReference.TryParse(definition.ClientSecret, out _)) throw new ArgumentException("MCP client secrets require a complete environment reference.");
            var redirect = definition.RedirectUri is { } uri ? new Uri(uri, UriKind.Absolute) : new Uri($"http://127.0.0.1:{definition.CallbackPort ?? FreePort()}/mcp/return");
            if (redirect.UserInfo.Length != 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0 ||
                redirect.Scheme != "https" && !(redirect.Scheme == "http" && redirect.IsLoopback)) throw new ArgumentException("Unsafe MCP OAuth callback address.");
            var metadata = definition.ClientMetadataUri is { } document ? new Uri(document, UriKind.Absolute) : null;
            if (metadata is not null && (metadata.Scheme != "https" || metadata.UserInfo.Length != 0 || metadata.Fragment.Length != 0)) throw new ArgumentException("MCP client metadata requires HTTPS.");
            var authority = definition.Authority is { } issuer ? new Uri(issuer, UriKind.Absolute) : null;
            if (authority is not null && (authority.Scheme != "https" || authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)) throw new ArgumentException("MCP OAuth authority requires HTTPS.");
            return new()
            {
                ClientId = definition.ClientId, ClientSecret = definition.ClientSecret is { } secret ? Expand(secret) : null,
                ClientMetadataDocumentUri = metadata, RedirectUri = redirect,
                Scopes = definition.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                AuthServerSelector = authority is null ? null : available => available.FirstOrDefault(candidate => candidate == authority),
                AuthorizationCallbackHandler = host.Authorize ?? new LoopbackOAuthCallback(host.OpenBrowser!).AuthorizeAsync,
                TokenCache = host.TokenCache?.Invoke(server, context),
            };
        }

        static int FreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        string Expand(string value)
        {
            if (!EnvironmentReference.TryParse(value, out var variable))
            {
                if (value.Contains("{env:", StringComparison.Ordinal)) throw new ArgumentException("MCP environment references must occupy the complete value.");
                return value;
            }
            var found = environment.FirstOrDefault(pair => string.Equals(pair.Key, variable, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(found) || found.Contains("{env:", StringComparison.Ordinal)) throw new ArgumentException("MCP environment reference is unavailable or recursive.");
            return found;
        }
    }
}