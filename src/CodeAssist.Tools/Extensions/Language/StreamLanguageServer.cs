using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using StreamJsonRpc;

namespace CodeAssist.Tools.Extensions;

public sealed class StreamLanguageServer : ILanguageService, IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _supportsDiagnostics;
    public string Server { get; }

    private StreamLanguageServer(JsonRpc rpc, string root, string server) { _rpc = rpc; _root = Path.GetFullPath(root); Server = server; }

    public static async Task<StreamLanguageServer> ConnectAsync(string server, string root, Stream output, Stream input, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server) || server.Length > 80) throw new ArgumentException("Invalid language server ID.");
        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(output, input, new SystemTextJsonFormatter()));
        var service = new StreamLanguageServer(rpc, root, server);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        rpc.StartListening();
        try
        {
            var result = await rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", new
            {
                processId = (int?)null, rootUri = new Uri(Path.GetFullPath(root)).AbsoluteUri,
                capabilities = new { general = new { positionEncodings = new[] { "utf-16" } }, textDocument = new { diagnostic = new { dynamicRegistration = false, relatedDocumentSupport = false } } },
                workspaceFolders = new[] { new { uri = new Uri(Path.GetFullPath(root)).AbsoluteUri, name = Path.GetFileName(root) } },
            }, deadline.Token);
            if (result.TryGetProperty("capabilities", out var capabilities) && capabilities.TryGetProperty("positionEncoding", out var encoding) && encoding.GetString() != "utf-16")
                throw new IOException("Language server must support UTF-16 positions.");
            service._supportsDiagnostics = capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("diagnosticProvider", out var diagnostics) && diagnostics.ValueKind is JsonValueKind.Object or JsonValueKind.True;
            await rpc.NotifyWithParameterObjectAsync("initialized", new { });
            return service;
        }
        catch { rpc.Dispose(); throw; }
    }

    public async Task<JsonElement> QueryAsync(LanguageQuery query, ToolContext context, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = new PathGuard(_root, context.ProtectedGlobs).Resolve(query.Path);
        if (path.Outside || path.Protected || new PathGuard(_root).Resolve(context.Workspace).Outside) throw new UnauthorizedAccessException("Language query is outside its configured workspace or protected.");
        if (query.Operation == "diagnostics" && !_supportsDiagnostics) return JsonSerializer.SerializeToElement(new { status = "unsupported" });
        var method = query.Operation switch
        {
            "definition" => "textDocument/definition", "references" => "textDocument/references", "hover" => "textDocument/hover",
            "documentSymbols" => "textDocument/documentSymbol", "workspaceSymbols" => "workspace/symbol", "implementation" => "textDocument/implementation",
            "incomingCalls" or "outgoingCalls" => "textDocument/prepareCallHierarchy", "diagnostics" => "textDocument/diagnostic",
            _ => throw new ArgumentException("Unsupported language operation."),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.ProcessTimeout);
        await _gate.WaitAsync(deadline.Token);
        try
        {
            var uri = new Uri(path.Full).AbsoluteUri;
            var version = _versions.GetValueOrDefault(uri) + 1;
            if (version == 1) await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = query.Language, version, text = query.Text } });
            else await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = new[] { new { text = query.Text } } });
            _versions[uri] = version;
            object parameters = query.Operation switch
            {
                "workspaceSymbols" => new { query = query.Search ?? "" },
                "documentSymbols" or "diagnostics" => new { textDocument = new { uri } },
                "references" => new { textDocument = new { uri }, position = new { line = query.Line - 1, character = query.Column - 1 }, context = new { includeDeclaration = true } },
                _ => new { textDocument = new { uri }, position = new { line = query.Line - 1, character = query.Column - 1 } },
            };
            var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters, deadline.Token);
            if (query.Operation == "diagnostics") return JsonSerializer.SerializeToElement(new { status = "ready", documentVersion = version, report = result });
            if (query.Operation is "incomingCalls" or "outgoingCalls")
            {
                if (result.ValueKind == JsonValueKind.Null || result.GetArrayLength() == 0) return JsonSerializer.SerializeToElement(Array.Empty<object>());
                if (result.GetArrayLength() > 20) throw new IOException("Call hierarchy exceeds its candidate budget.");
                var calls = new List<JsonElement>();
                foreach (var item in result.EnumerateArray())
                    calls.Add(await _rpc.InvokeWithParameterObjectAsync<JsonElement>("callHierarchy/" + query.Operation, new { item }, deadline.Token));
                return JsonSerializer.SerializeToElement(calls);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _rpc.InvokeWithParameterObjectAsync<JsonElement>("shutdown", null, timeout.Token);
                await _rpc.NotifyWithParameterObjectAsync("exit", null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            finally { _rpc.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}