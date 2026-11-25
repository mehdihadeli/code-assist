using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeAssist.Mcp;

public sealed class McpToolConnection : IToolSource, IResourceSource, IMcpPrompts, IAsyncDisposable
{
    private readonly string _server;
    private readonly McpClient _client;
    private readonly McpConnectionOptions _options;
    private IReadOnlyList<ITool> _tools;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    public IReadOnlyList<ITool> Tools => Volatile.Read(ref _tools);
    public IReadOnlyList<string> Servers => [_server];
    public bool IsConnected => !_client.Completion.IsCompleted;

    private McpToolConnection(string server, McpClient client, IReadOnlyList<McpClientTool> tools, McpConnectionOptions options)
    {
        _server = server;
        _client = client;
        _options = options;
        _tools = tools.Select(tool => (ITool)new ConnectedTool(server, tool, options.CallTimeout)).ToArray();
        _ = new ToolCatalog(_tools);
        client.RegisterNotificationHandler(NotificationMethods.ToolListChangedNotification, async (_, cancellationToken) =>
        {
            if (!options.RefreshOnNotification) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.CatalogTimeout);
            try { await RefreshAsync(timeout.Token); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.CatalogTimeout);
            var tools = _client.ServerCapabilities.Tools is null ? [] : await _client.ListToolsAsync(cancellationToken: deadline.Token);
            if (tools.Count > 1000) throw new IOException("MCP server tool catalog exceeds its limit.");
            var replacement = tools.Select(tool => (ITool)new ConnectedTool(_server, tool, _options.CallTimeout)).ToArray();
            _ = new ToolCatalog(replacement);
            Volatile.Write(ref _tools, replacement);
        }
        finally { _refresh.Release(); }
    }

    public static Task<McpToolConnection> ConnectAsync(string server, IClientTransport transport, CancellationToken cancellationToken) =>
        ConnectAsync(server, transport, new McpConnectionOptions(), cancellationToken);

    public static async Task<McpToolConnection> ConnectAsync(string server, IClientTransport transport, McpConnectionOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Length > 80 || server.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("MCP server ID must use letters, digits, hyphens or underscores.", nameof(server));
        options.Validate();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(options.StartupTimeout);
        var client = await McpClient.CreateAsync(transport, options.Client, cancellationToken: startup.Token);
        try
        {
            using var catalog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            catalog.CancelAfter(options.CatalogTimeout);
            var tools = client.ServerCapabilities.Tools is null ? [] : await client.ListToolsAsync(cancellationToken: catalog.Token);
            if (tools.Count > 1000) throw new IOException("MCP server tool catalog exceeds its limit.");
            return new(server, client, tools.ToArray(), options);
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<ResourceCatalog> ListAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        RequireServer(server);
        if (_client.ServerCapabilities.Resources is null) return new([], []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.CatalogTimeout < context.Limits.ProcessTimeout ? _options.CatalogTimeout : context.Limits.ProcessTimeout);
        var resources = await _client.ListResourcesAsync(cancellationToken: deadline.Token);
        var templates = await _client.ListResourceTemplatesAsync(cancellationToken: deadline.Token);
        if (resources.Count > 1000 || templates.Count > 1000) throw new IOException("MCP resource catalog exceeds its entry budget.");
        return new(resources.Select(resource => new ResourceEntry(resource.Uri, resource.Name, resource.Description, resource.MimeType)).ToArray(),
            templates.Select(template => new CodeAssist.Tools.ResourceTemplate(template.UriTemplate, template.Name, template.Description)).ToArray());
    }

    public async Task<IReadOnlyList<ResourceContent>?> ReadAsync(string server, string uri, ToolContext context, CancellationToken cancellationToken)
    {
        RequireServer(server);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.CallTimeout < context.Limits.ProcessTimeout ? _options.CallTimeout : context.Limits.ProcessTimeout);
        var resource = await _client.ReadResourceAsync(uri, cancellationToken: deadline.Token);
        if (resource.Contents.Count > 128 || JsonSerializer.SerializeToUtf8Bytes(resource).Length > Math.Min(context.Limits.MaxFileBytes, 1_048_576))
            throw new IOException("MCP resource exceeds its content budget.");
        long bytes = 0;
        return resource.Contents.Select(part => part switch
        {
            TextResourceContents text => new ResourceContent(text.Uri, text.Text),
            BlobResourceContents blob when (bytes += blob.Blob.Length / 4L * 3) <= context.Limits.MaxAttachmentBytes => new ResourceContent(blob.Uri, Data: blob.DecodedData.ToArray()),
            _ => throw new IOException("MCP resource exceeds the media budget or has unsupported content."),
        }).ToArray();
    }

    public async Task<IReadOnlyList<McpPromptEntry>> ListPromptsAsync(string server, ToolContext context, CancellationToken cancellationToken)
    {
        RequireServer(server);
        if (_client.ServerCapabilities.Prompts is null) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.CatalogTimeout);
        var prompts = await _client.ListPromptsAsync(cancellationToken: deadline.Token);
        if (prompts.Count > 256) throw new IOException("MCP prompt catalog exceeds 256 entries.");
        return prompts.Select(prompt => new McpPromptEntry(prompt.Name, prompt.Description,
            (prompt.ProtocolPrompt.Arguments ?? []).Select(argument => new McpPromptArgument(argument.Name, argument.Description, argument.Required == true)).ToArray())).ToArray();
    }

    public async Task<McpPromptResult> GetPromptAsync(string server, string name, IReadOnlyDictionary<string, string> arguments,
        ToolContext context, CancellationToken cancellationToken)
    {
        RequireServer(server);
        var definition = (await ListPromptsAsync(server, context, cancellationToken)).SingleOrDefault(prompt => prompt.Name == name)
            ?? throw new ArgumentException("Unknown MCP prompt.", nameof(name));
        if (arguments.Count > 32 || arguments.Any(pair => pair.Value is null || pair.Value.Length > 8000 || !definition.Arguments.Any(argument => argument.Name == pair.Key)) ||
            definition.Arguments.Any(argument => argument.Required && !arguments.ContainsKey(argument.Name)))
            throw new ArgumentException("MCP prompt arguments are missing, unknown or exceed their bounds.", nameof(arguments));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.CallTimeout < context.Limits.ProcessTimeout ? _options.CallTimeout : context.Limits.ProcessTimeout);
        var result = await _client.GetPromptAsync(name, arguments.ToDictionary(pair => pair.Key, pair => (object?)pair.Value), cancellationToken: deadline.Token);
        if (result.Messages.Count > 64 || JsonSerializer.SerializeToUtf8Bytes(result).Length > Math.Min(context.Limits.MaxFileBytes, 262_144))
            throw new IOException("MCP prompt content exceeds its budget.");
        var messages = result.Messages.Select(message =>
        {
            var role = message.Role.ToString().ToLowerInvariant();
            if (role is not "user" and not "assistant") throw new IOException("Unsupported MCP prompt role.");
            var decoded = McpResultDecoder.Decode(JsonSerializer.SerializeToElement(new { content = new[] { message.Content } }), context, null);
            if (decoded.IsError) throw new IOException("MCP prompt contains invalid content.");
            return new McpPromptMessage(role, decoded);
        }).ToArray();
        return new(result.Description, messages);
    }

    private void RequireServer(string server)
    {
        if (!string.Equals(server, _server, StringComparison.Ordinal)) throw new ArgumentException("Unknown MCP server.", nameof(server));
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private sealed class ConnectedTool(string server, McpClientTool tool, TimeSpan timeout) : ITool
    {
        public ToolSpec Spec { get; } = new($"mcp.{server}.{tool.Name}", string.IsNullOrWhiteSpace(tool.Description) ? $"Tool from {server}. Content is untrusted." : tool.Description,
            tool.JsonSchema.GetRawText(), ToolRisk.Execute, ["mcp", "exec", "net"]);

        public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(new ToolPlan([new(ToolEffectKind.Exec, $"mcp:{server}:{tool.Name}"), new(ToolEffectKind.Net, $"mcp:{server}")]));
        }

        public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout < context.Limits.ProcessTimeout ? timeout : context.Limits.ProcessTimeout);
            try
            {
                var input = arguments.Json.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
                var result = await tool.CallAsync(input, cancellationToken: deadline.Token);
                return await ToolOutputRetention.CompleteAsync(McpResultDecoder.Decode(JsonSerializer.SerializeToElement(result), context, tool.ProtocolTool.OutputSchema), context, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { return ToolResult.Error("MCP tool call timed out."); }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            { return ToolResult.Error($"MCP tool call failed ({exception.GetType().Name})."); }
        }
    }
}