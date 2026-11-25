using CodeAssist.Mcp;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class RemoteDiscoveryTests
{
    [Fact]
    public async Task Should_FilterRemoteMetadataAndExplainInvocation_When_DiscoveringAuthenticatedTools()
    {
        var connections = Substitute.For<IMcpConnections>();
        connections.Servers.Returns(new[] { "docs" });
        ToolSpec[] specs = [new("mcp.docs.lookup", "Look up documentation", "{}", ToolRisk.Read, ["read"]), new("mcp.docs.erase", "Erase content", "{}", ToolRisk.Write, ["write"])];
        connections.ListAsync("docs", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(specs);
        var tool = new ToolFindTool(_ => [new("mcp.remote", "Remote", "{}", ToolRisk.Execute, ["exec"])], connections,
            (_, spec) => ToolCatalog.IsVisible(spec, ["read"], ["write"]));
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var result = await tool.ExecuteAsync(ToolArguments.Parse("""{"server":"docs","includeSchemas":true}"""), context, TestContext.Current.CancellationToken);
        result.IsError.ShouldBeFalse(result.Text);
        using var json = JsonDocument.Parse(result.Text);
        json.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        result.Text.ShouldContain("mcp.docs.lookup");
        result.Text.ShouldContain("mcp.remote");
        result.Text.ShouldNotContain("erase");
        var remote = new McpRemoteTool(connections, (_, spec) => ToolCatalog.IsVisible(spec, ["read"], ["write"]));
        (await remote.ExecuteAsync(ToolArguments.Parse("""{"operation":"call","server":"docs","tool":"erase","input":{}}"""), context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        await connections.DidNotReceive().CallAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolArguments>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        var denied = new ToolFindTool(_ => [], connections, (_, _) => true);
        (await denied.ExecuteAsync(ToolArguments.Parse("""{"server":"docs"}"""), context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
    }
}