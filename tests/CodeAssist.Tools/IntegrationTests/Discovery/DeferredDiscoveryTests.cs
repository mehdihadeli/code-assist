using CodeAssist.Mcp;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Server.Composition;
using CodeAssist.Configuration;
using CodeAssist.Models.Chat;
using System.Text.Json;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class DeferredDiscoveryTests
{
    [Fact]
    public async Task Should_ChangeActualModelRequestSchemas_When_ActivatingDiscovery()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/chat/completions").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"id":"fixture","object":"chat.completion","model":"fixture","choices":[{"index":0,"message":{"role":"assistant","content":"Done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12}}"""));
        var exposure = new SessionToolExposure();
        ToolSpec[] allowed = [new("tool.find", "Discover", "{\"type\":\"object\"}", ToolRisk.Read, ["read"]), new("workspace.changes", "Git", "{\"type\":\"object\"}", ToolRisk.Execute, ["git"])];
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var adapter = new ChatClientAgentModel(_ => ProviderComposition.CreateChatClient("default", "fixture", new ProviderSettings { Endpoint = server.Url!, Api = ProviderApi.Chat }, ProviderRegistry.CreateDefault(), new Dictionary<string, string>()), exposure);
        await adapter.RespondAsync(new("default/fixture", "Inspect changes", [], allowed, 1000), context, TestContext.Current.CancellationToken);
        exposure.Activate([allowed[1]], context);
        await adapter.RespondAsync(new("default/fixture", "Inspect changes", [], allowed, 1000), context, TestContext.Current.CancellationToken);
        var bodies = server.LogEntries.Select(entry => JsonDocument.Parse(entry.RequestMessage.ShouldNotBeNull().Body!)).ToArray();
        try
        {
            bodies.Length.ShouldBe(2);
            bodies[0].RootElement.GetProperty("tools").GetArrayLength().ShouldBe(1);
            bodies[1].RootElement.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ShouldContain("workspace.changes");
        }
        finally { foreach (var body in bodies) body.Dispose(); }
    }

    [Fact]
    public async Task Should_ActivateOnlyAllowedSessionSchemas_When_DiscoveringTools()
    {
        var exposure = new SessionToolExposure();
        ToolSpec[] allowed = [new("tool.find", "Discover", "{}", ToolRisk.Read, ["read"]), new("workspace.changes", "Git changes", "{}", ToolRisk.Execute, ["git"]), new("shell.run", "Shell", "{}", ToolRisk.Execute, ["exec"])];
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        exposure.Select(allowed, context).Select(tool => tool.Name).ShouldBe(["tool.find"]);
        var find = new ToolFindTool(_ => allowed, exposure: exposure);
        var result = await find.ExecuteAsync(ToolArguments.Parse("""{"query":"git","activate":true}"""), context, TestContext.Current.CancellationToken);
        result.IsError.ShouldBeFalse(result.Text);
        exposure.Select(allowed, context).Select(tool => tool.Name).ShouldBe(["tool.find", "workspace.changes"]);
        exposure.Select(allowed, new ToolContext { Workspace = context.Workspace, SessionId = Guid.NewGuid() }).Select(tool => tool.Name).ShouldBe(["tool.find"]);
        exposure.Select([allowed[0]], context).Select(tool => tool.Name).ShouldBe(["tool.find"]);
        exposure.Select([allowed[1]], context).Select(tool => tool.Name).ShouldBe(["workspace.changes"]);
        var firstLarge = allowed[1] with { Name = "first.large", Description = new string('x', 60000) };
        var secondLarge = allowed[1] with { Name = "second.large", Description = new string('y', 60000) };
        exposure.Activate([firstLarge], context);
        exposure.Activate([secondLarge], context);
        exposure.Select([allowed[0], firstLarge, secondLarge], context).Select(tool => tool.Name).ShouldBe(["tool.find", "second.large"]);
    }
}