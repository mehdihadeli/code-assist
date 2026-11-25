using System.IO.Pipes;
using CodeAssist.Tools;
using CodeAssist.Tools.Extensions;
using CodeAssist.Workspaces.Platform;
using Shouldly;
using StreamJsonRpc;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class LanguageServerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_SynchronizeDocumentsAndConvertPositions_When_QueryingRealRpcTransport(bool supportsDiagnostics)
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-lsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var name = "ca-rpc-" + Guid.NewGuid().ToString("N");
            await using var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await using var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connecting = clientStream.ConnectAsync(TestContext.Current.CancellationToken);
            await serverStream.WaitForConnectionAsync(TestContext.Current.CancellationToken);
            await connecting;
            var fixture = new LanguageServerFixture { SupportsDiagnostics = supportsDiagnostics };
            using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverStream, serverStream, new SystemTextJsonFormatter()));
            server.AddLocalRpcTarget(fixture);
            server.StartListening();
            await using var language = await StreamLanguageServer.ConnectAsync("fixture", root, clientStream, clientStream, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var tool = new LanguageQueryTool(language, new LocalFileSystem());
            var context = new ToolContext { Workspace = root };
            var path = Path.Combine(root, "code.cs");
            await File.WriteAllTextAsync(path, "class First {}", TestContext.Current.CancellationToken);
            var input = ToolArguments.Parse("""{"operation":"hover","path":"code.cs","language":"csharp","line":2,"column":1}""");
            var result = await tool.ExecuteAsync(input, context, TestContext.Current.CancellationToken);
            result.IsError.ShouldBeFalse(result.Text);
            result.Text.ShouldContain("symbol details");
            result.Text.ShouldContain("\"line\":1");
            await File.WriteAllTextAsync(path, "class Second {}", TestContext.Current.CancellationToken);
            (await tool.ExecuteAsync(input, context, TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();
            fixture.Texts.ShouldBe(["class First {}", "class Second {}"]);
            var diagnostics = await new CodeDiagnosticsTool(language, new LocalFileSystem()).ExecuteAsync(ToolArguments.Parse("""{"path":"code.cs","language":"csharp"}"""), context, TestContext.Current.CancellationToken);
            diagnostics.IsError.ShouldBe(!supportsDiagnostics, diagnostics.Text);
            if (supportsDiagnostics)
            {
                var payload = diagnostics.StructuredContent.ShouldNotBeNull();
                payload.GetProperty("documentVersion").GetInt32().ShouldBe(3);
                payload.GetProperty("findings")[0].GetProperty("line").GetInt32().ShouldBe(1);
                payload.GetProperty("findings")[0].GetProperty("code").GetString().ShouldBe("CS1000");
            }
            foreach (var operation in new[] { "definition", "references", "implementation", "documentSymbols", "workspaceSymbols", "incomingCalls", "outgoingCalls" })
            {
                var query = ToolArguments.Parse(System.Text.Json.JsonSerializer.Serialize(new { operation, path = "code.cs", language = "csharp", query = "First" }));
                var answer = await tool.ExecuteAsync(query, context, TestContext.Current.CancellationToken);
                answer.IsError.ShouldBeFalse(operation + ": " + answer.Text);
                answer.Text.ShouldNotBe("null");
            }
            await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"hover","path":"../escape","language":"csharp"}"""), context, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }
}