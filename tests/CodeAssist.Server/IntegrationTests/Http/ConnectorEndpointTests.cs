using CodeAssist.Mcp;
using System.Net;
using System.Net.Http.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Server.IntegrationTests.Http;

public sealed class ConnectorEndpointTests
{
    [Fact]
    public async Task Should_RequireExplicitStartupAndReleaseSessionHandles_When_ManagingConnectorsOverHttp()
    {
        var connections = Substitute.For<IMcpConnections>();
        connections.Servers.Returns(["fixture"]);
        connections.StatusAsync("fixture", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new McpConnectionStatus("fixture", false, 0));
        connections.ConnectAsync("fixture", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new McpConnectionStatus("fixture", true, 2));
        var prompts = Substitute.For<IMcpPrompts>();
        prompts.ListPromptsAsync("fixture", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new[] { new McpPromptEntry("inspect", "Untrusted prompt", []) });
        await using var server = await ServerFixture.StartAsync(connectors: new(connections, id => new() { Workspace = Path.GetTempPath(), SessionId = id }, prompts));
        var session = await server.CreateAsync();
        var path = $"/v1/sessions/{session.Id}/connectors/fixture";
        using var rejected = await server.Http.PostAsJsonAsync(path + "/connection", new CreateConnectorConnectionRequest(), TestContext.Current.CancellationToken);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await connections.DidNotReceive().ConnectAsync(Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        using var created = await server.Http.PostAsJsonAsync(path + "/connection", new CreateConnectorConnectionRequest(true), TestContext.Current.CancellationToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldBe(path + "/connection");
        (await server.Http.GetFromJsonAsync<McpPromptEntry[]>(path + "/prompts", TestContext.Current.CancellationToken))!.Single().Name.ShouldBe("inspect");
        using var noExecution = await server.Http.PostAsJsonAsync(path + "/call", new { tool = "delete" }, TestContext.Current.CancellationToken);
        noExecution.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var removed = await server.Http.DeleteAsync($"/v1/sessions/{session.Id}", TestContext.Current.CancellationToken);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await connections.Received(1).ReleaseSessionAsync(Arg.Is<ToolContext>(context => context.SessionId == session.Id), Arg.Any<CancellationToken>());
        using var stale = await server.Http.GetAsync(path + "/connection", TestContext.Current.CancellationToken);
        stale.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var openApi = await server.Http.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        (await openApi.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("/connectors/{server}/connection");
    }
}