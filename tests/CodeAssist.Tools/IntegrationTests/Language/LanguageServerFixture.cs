using System.Text.Json;
using StreamJsonRpc;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class LanguageServerFixture
{
    public List<string> Texts { get; } = [];
    public bool SupportsDiagnostics { get; set; } = true;

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public object Initialize(JsonElement parameters) => new { capabilities = new { positionEncoding = "utf-16", textDocumentSync = 1, diagnosticProvider = SupportsDiagnostics ? new { interFileDependencies = false, workspaceDiagnostics = false } : (object?)null } };

    [JsonRpcMethod("textDocument/diagnostic", UseSingleObjectParameterDeserialization = true)]
    public object Diagnostics(JsonElement parameters) => new { kind = "full", items = new[] { new { range = Range, severity = 1, code = "CS1000", message = "Fixture compilation error" } } };

    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
    public void Initialized(JsonElement parameters) { }

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void Open(JsonElement parameters) => Texts.Add(parameters.GetProperty("textDocument").GetProperty("text").GetString()!);

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void Change(JsonElement parameters) => Texts.Add(parameters.GetProperty("contentChanges")[0].GetProperty("text").GetString()!);

    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public object Hover(JsonElement parameters) => new { contents = "symbol details", line = parameters.GetProperty("position").GetProperty("line").GetInt32() };

    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public object Definition(JsonElement parameters) => new[] { new { uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString(), range = Range } };

    [JsonRpcMethod("textDocument/references", UseSingleObjectParameterDeserialization = true)]
    public object References(JsonElement parameters) => Definition(parameters);

    [JsonRpcMethod("textDocument/implementation", UseSingleObjectParameterDeserialization = true)]
    public object Implementation(JsonElement parameters) => Definition(parameters);

    [JsonRpcMethod("textDocument/documentSymbol", UseSingleObjectParameterDeserialization = true)]
    public object DocumentSymbols(JsonElement parameters) => new[] { new { name = "First", kind = 5, range = Range, selectionRange = Range } };

    [JsonRpcMethod("workspace/symbol", UseSingleObjectParameterDeserialization = true)]
    public object WorkspaceSymbols(JsonElement parameters) => new[] { new { name = parameters.GetProperty("query").GetString(), kind = 5, location = new { uri = "file:///fixture.cs", range = Range } } };

    [JsonRpcMethod("textDocument/prepareCallHierarchy", UseSingleObjectParameterDeserialization = true)]
    public object PrepareHierarchy(JsonElement parameters) => new[] { new { name = "First", kind = 12, uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString(), range = Range, selectionRange = Range } };

    [JsonRpcMethod("callHierarchy/incomingCalls", UseSingleObjectParameterDeserialization = true)]
    public object Incoming(JsonElement parameters) => new[] { new { from = parameters.GetProperty("item"), fromRanges = new[] { Range } } };

    [JsonRpcMethod("callHierarchy/outgoingCalls", UseSingleObjectParameterDeserialization = true)]
    public object Outgoing(JsonElement parameters) => new[] { new { to = parameters.GetProperty("item"), fromRanges = new[] { Range } } };

    private static object Range => new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 1 } };

    [JsonRpcMethod("shutdown")]
    public object? Shutdown() => null;

    [JsonRpcMethod("exit")]
    public void Exit() { }
}