using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeAssist.Client;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class AcpConnection(CliContext cli, Uri? endpoint, string? model, TextReader input, TextWriter output) : IAsyncDisposable
{
    private const int MaxFrameCharacters = 262144;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Dictionary<string, ManagedServer> _workspaces = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<Guid, Target> _sessions = [];
    private readonly ConcurrentDictionary<Guid, Task> _turns = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly CancellationTokenSource _closed = new();
    private bool _initialized;
    private bool _attached;

    public async Task RunAsync(CancellationToken cancellation = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _closed.Token);
        try
        {
            while (await ReadFrameAsync(lifetime.Token) is { } frame)
            {
                JsonElement request;
                try { request = JsonSerializer.Deserialize<JsonElement>(frame); }
                catch (JsonException)
                {
                    await ErrorAsync(null, -32700, "Invalid JSON.", lifetime.Token);
                    continue;
                }
                if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                    !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                {
                    await ErrorAsync(null, -32600, "Invalid JSON-RPC request.", lifetime.Token);
                    continue;
                }
                var hasId = request.TryGetProperty("id", out var id);
                if (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                {
                    await ErrorAsync(null, -32600, "Invalid request identifier.", lifetime.Token);
                    continue;
                }
                var parameters = request.TryGetProperty("params", out var supplied) ? supplied : JsonSerializer.SerializeToElement(new { });
                if (method.GetString() == "session/cancel")
                {
                    if (TrySession(parameters, out var session) && _cancellations.TryGetValue(session, out var active))
                        await active.CancelAsync();
                    continue;
                }
                if (!hasId) continue;
                if (method.GetString() == "session/prompt" && _initialized)
                {
                    if (!TrySession(parameters, out var session) || !_sessions.TryGetValue(session, out var target))
                    {
                        await ErrorAsync(id, -32002, "Unknown session.", lifetime.Token);
                        continue;
                    }
                    if (_turns.TryGetValue(session, out var active) && !active.IsCompleted)
                    {
                        await ErrorAsync(id, -32000, "A turn is already running.", lifetime.Token);
                        continue;
                    }
                    var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    if (_cancellations.TryGetValue(session, out var previous)) previous.Dispose();
                    _cancellations[session] = turnCancellation;
                    _turns[session] = PromptAsync(id.Clone(), session, target, parameters.Clone(), turnCancellation.Token, lifetime.Token);
                    continue;
                }
                try
                {
                    var result = await DispatchAsync(method.GetString()!, parameters, lifetime.Token);
                    await WriteAsync(new { jsonrpc = "2.0", id, result }, lifetime.Token);
                }
                catch (Exception exception) when (exception is not IOException && !lifetime.IsCancellationRequested)
                {
                    var (code, message) = exception switch
                    {
                        AcpException protocol => (protocol.Code, protocol.Message),
                        ArgumentException or JsonException or InvalidOperationException => (-32602, "Invalid parameters."),
                        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => (-32002, "Unknown session."),
                        _ => (-32603, "Operation failed. Check service configuration."),
                    };
                    await ErrorAsync(id, code, message, lifetime.Token);
                }
            }
        }
        finally
        {
            await _closed.CancelAsync();
            await Task.WhenAll(_turns.Values);
        }
    }

    private async Task<object> DispatchAsync(string method, JsonElement parameters, CancellationToken cancellation)
    {
        if (method == "initialize")
        {
            if (_initialized) throw new AcpException(-32600, "Connection is already initialized.");
            if (!parameters.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var requested) || requested < 1)
                throw new AcpException(-32602, "A positive protocolVersion is required.");
            _initialized = true;
            return new
            {
                protocolVersion = 1,
                agentInfo = new { name = "code-assist", title = "CodeAssist", version = typeof(AcpConnection).Assembly.GetName().Version?.ToString() ?? "0.0.0" },
                agentCapabilities = new
                {
                    loadSession = true,
                    promptCapabilities = new { embeddedContext = true, image = false, audio = false },
                    sessionCapabilities = new { list = new { }, resume = new { }, delete = new { } },
                },
                authMethods = Array.Empty<object>(),
            };
        }
        if (!_initialized) throw new AcpException(-32000, "Initialize the connection first.");
        if (parameters.ValueKind != JsonValueKind.Object) throw new AcpException(-32602, "Parameters must be an object.");
        switch (method)
        {
            case "session/new":
            case "session/load":
            case "session/resume":
                var directory = Workspace(parameters);
                ValidateSessionOptions(parameters);
                if (method != "session/new" && _sessions.TryGetValue(SessionId(parameters), out var previousTarget) && !SameDirectory(directory, previousTarget.Directory))
                    throw new AcpException(-32602, "Session workspace cannot change.");
                var client = await ClientAsync(directory, cancellation);
                var session = method == "session/new"
                    ? await client.CreateAsync(model, cancellation)
                    : await client.SessionAsync(SessionId(parameters), cancellation);
                if (_sessions.TryGetValue(session.Id, out var known) && !SameDirectory(directory, known.Directory))
                    throw new AcpException(-32602, "Session workspace cannot change.");
                _sessions[session.Id] = new(client, directory);
                if (method == "session/load")
                    foreach (var message in await client.MessagesAsync(session.Id, cancellation))
                        await UpdateAsync(session.Id, message.Role == "user" ? "user_message_chunk" : "agent_message_chunk", message.Text, cancellation);
                return method == "session/new" ? new { sessionId = session.Id.ToString() } : new { };
            case "session/list":
                if (parameters.TryGetProperty("cursor", out var cursor) && cursor.ValueKind != JsonValueKind.Null)
                    throw new AcpException(-32602, "This connection returns a complete bounded list; no cursor is available.");
                var filter = parameters.TryGetProperty("cwd", out var requestedDirectory) && requestedDirectory.ValueKind != JsonValueKind.Null ? Workspace(parameters) : null;
                var listed = new List<object>();
                foreach (var group in _sessions.GroupBy(item => item.Value.Client))
                    foreach (var item in await group.Key.SessionsAsync(cancellation))
                        if (_sessions.TryGetValue(item.Id, out var target) && (filter is null || SameDirectory(filter, target.Directory)))
                            listed.Add(new { sessionId = item.Id.ToString(), cwd = target.Directory, updatedAt = item.CreatedAt.ToString("O") });
                return new { sessions = listed };
            case "session/delete":
                var id = SessionId(parameters);
                if (!_sessions.TryGetValue(id, out var selected)) throw new AcpException(-32002, "Unknown session.");
                await selected.Client.DeleteSessionAsync(id, cancellation);
                _sessions.Remove(id);
                return new { };
            default:
                throw new AcpException(-32601, "Method not supported.");
        }
    }

    private async Task PromptAsync(JsonElement id, Guid session, Target target, JsonElement parameters, CancellationToken turnCancellation, CancellationToken cancellation)
    {
        try
        {
            var prompt = Prompt(parameters);
            await foreach (var update in target.Client.StreamAsync(session, prompt, turnCancellation))
            {
                if (update.Kind == "text.delta" && update.Text is not null)
                    await UpdateAsync(session, "agent_message_chunk", update.Text, cancellation);
                if (update.Kind is "turn.completed" or "turn.canceled")
                {
                    await WriteAsync(new { jsonrpc = "2.0", id, result = new { stopReason = update.Kind == "turn.canceled" ? "cancelled" : "end_turn" } }, cancellation);
                    return;
                }
                if (update.Kind == "turn.failed") throw new AcpException(-32603, "Turn failed. Inspect committed history before retrying.");
            }
            throw new AcpException(-32603, "Turn ended without completion.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            await WriteAsync(new { jsonrpc = "2.0", id, result = new { stopReason = "cancelled" } }, cancellation);
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested)
        {
            await ErrorAsync(id, exception is AcpException protocol ? protocol.Code : -32603,
                exception is AcpException error ? error.Message : "Turn failed. Check service configuration.", cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task<CodeAssistClient> ClientAsync(string directory, CancellationToken cancellation)
    {
        if (!cli.Snapshot.IsValid) throw new AcpException(-32000, "Settings are invalid. Run configuration diagnostics.");
        if (endpoint is not null)
        {
            if (!SameDirectory(directory, cli.Settings.Locations.WorkingDirectory))
                throw new AcpException(-32602, "Attached ACP sessions must use the service working directory.");
            if (!_attached)
            {
                await cli.ConnectAsync(endpoint, cancellation);
                _attached = true;
            }
            return cli.Api;
        }
        if (_workspaces.TryGetValue(directory, out var existing)) return existing.Client;
        if (_workspaces.Count >= 4) throw new AcpException(-32000, "Workspace capacity reached. Restart the ACP connection.");
        var server = await cli.LaunchServerAsync(cancellation: cancellation, workingDirectory: directory);
        _workspaces.Add(directory, server);
        return server.Client;
    }

    private static void ValidateSessionOptions(JsonElement parameters)
    {
        foreach (var name in new[] { "mcpServers", "additionalDirectories" })
            if (parameters.TryGetProperty(name, out var values) && (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 0))
                throw new AcpException(-32602, "Client-supplied servers and additional roots are not supported. Use trusted connector settings.");
        if (parameters.TryGetProperty("systemPrompt", out var system) && system.ValueKind != JsonValueKind.Null)
            throw new AcpException(-32602, "System prompt overrides are not supported.");
    }

    private static string Workspace(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("cwd", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } directory || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new AcpException(-32602, "cwd must identify an existing absolute directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    private static bool SameDirectory(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static Guid SessionId(JsonElement parameters) => TrySession(parameters, out var session)
        ? session : throw new AcpException(-32602, "A valid sessionId is required.");

    private static bool TrySession(JsonElement parameters, out Guid session)
    {
        session = default;
        return parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("sessionId", out var id) &&
            id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out session);
    }

    private static string Prompt(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("prompt", out var blocks) || blocks.ValueKind != JsonValueKind.Array || blocks.GetArrayLength() == 0)
            throw new AcpException(-32602, "A nonempty prompt array is required.");
        var result = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            var type = RequiredString(block, "type");
            string text;
            switch (type)
            {
                case "text": text = RequiredString(block, "text"); break;
                case "resource_link": text = "Resource reference (not fetched): " + RequiredString(block, "uri"); break;
                case "resource":
                    if (!block.TryGetProperty("resource", out var resource)) throw new AcpException(-32602, "Embedded text resource is required.");
                    text = "Untrusted attached context:\n" + RequiredString(resource, "text");
                    break;
                default: throw new AcpException(-32602, "Only text, resource links and embedded text are supported.");
            }
            if (result.Length + text.Length + (result.Length > 0 ? 1 : 0) > 65536) throw new AcpException(-32602, "Prompt exceeds 65536 characters.");
            if (result.Length > 0) result.Append('\n');
            result.Append(text);
        }
        if (string.IsNullOrWhiteSpace(result.ToString())) throw new AcpException(-32602, "Prompt must not be empty.");
        return result.ToString();
    }

    private static string RequiredString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : throw new AcpException(-32602, "Content fields must have the expected types.");

    private Task UpdateAsync(Guid session, string kind, string text, CancellationToken cancellation) => WriteAsync(new
    {
        jsonrpc = "2.0", method = "session/update", @params = new
        {
            sessionId = session.ToString(), update = new { sessionUpdate = kind, content = new { type = "text", text } },
        },
    }, cancellation);

    private Task ErrorAsync(JsonElement? id, int code, string message, CancellationToken cancellation) =>
        WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, cancellation);

    private async Task WriteAsync(object value, CancellationToken cancellation)
    {
        await _writing.WaitAsync(cancellation);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonSerializerOptions.Web).AsMemory(), cancellation);
            await output.FlushAsync(cancellation);
        }
        finally { _writing.Release(); }
    }

    private async Task<string?> ReadFrameAsync(CancellationToken cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (await input.ReadAsync(buffer.AsMemory(), cancellation) != 0)
        {
            if (buffer[0] == '\n') return result.ToString();
            if (result.Length >= MaxFrameCharacters) throw new IOException("ACP frame exceeds the input limit.");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : throw new IOException("Truncated ACP frame.");
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync();
        foreach (var server in _workspaces.Values) await server.DisposeAsync();
        foreach (var cancellation in _cancellations.Values) cancellation.Dispose();
        _closed.Dispose();
        _writing.Dispose();
    }

    private sealed record Target(CodeAssistClient Client, string Directory);
    private sealed class AcpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}