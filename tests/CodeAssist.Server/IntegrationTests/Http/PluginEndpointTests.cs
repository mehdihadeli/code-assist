using System.Net;
using System.Net.Http.Json;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Server.IntegrationTests.Http;

public sealed class PluginEndpointTests
{
    [Fact]
    public async Task Should_ListAndExpandTextWithoutInference_When_PluginContentIsAvailable()
    {
        var plugins = Substitute.For<IPluginContent>();
        plugins.Commands.Returns([new PluginCommandDto("team.review/inspect", "prompt", "Review", 30, 50)]);
        plugins.ExpandPrompt("team.review/inspect", "task").Returns("Review\nUser input: task");
        await using var server = await ServerFixture.StartAsync(plugins: plugins);
        var entries = await server.Http.GetFromJsonAsync<PluginCommandDto[]>("/v1/plugins/commands", TestContext.Current.CancellationToken);
        entries.ShouldNotBeNull().Single().Name.ShouldBe("team.review/inspect");
        using var response = await server.Http.PostAsJsonAsync("/v1/plugins/expand", new ExpandPluginRequest("team.review/inspect", "task"), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PluginPromptDto>(TestContext.Current.CancellationToken)).ShouldNotBeNull().Text.ShouldContain("task");
        server.Clients.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_RequireBearerAndExposeNoInstallRoute_When_ClientAccessesPlugins()
    {
        await using var server = await ServerFixture.StartAsync();
        using var unavailable = await server.Http.PostAsJsonAsync("/v1/plugins/install", new { source = "untrusted" }, TestContext.Current.CancellationToken);
        unavailable.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        server.Http.DefaultRequestHeaders.Authorization = null;
        using var response = await server.Http.GetAsync("/v1/plugins/commands", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_RejectInvalidExpansion_When_InputIsUnboundedOrPluginMissing()
    {
        await using var server = await ServerFixture.StartAsync();
        using var invalid = await server.Http.PostAsJsonAsync("/v1/plugins/expand", new ExpandPluginRequest("", "task"), TestContext.Current.CancellationToken);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var tooLong = await server.Http.PostAsJsonAsync("/v1/plugins/expand", new ExpandPluginRequest("team.review/inspect", new string('x', 65_537)), TestContext.Current.CancellationToken);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var missing = await server.Http.PostAsJsonAsync("/v1/plugins/expand", new ExpandPluginRequest("team.review/inspect"), TestContext.Current.CancellationToken);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        server.Clients.ShouldBeEmpty();
    }
}