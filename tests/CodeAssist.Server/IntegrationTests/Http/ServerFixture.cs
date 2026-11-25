using CodeAssist.Models;
using CodeAssist.Mcp;
using CodeAssist.Extensions.Skills;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Server.Http;
using CodeAssist.Server.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Server.IntegrationTests.Http;

internal sealed class ServerFixture : IAsyncDisposable
{
    public const string Token = "test-only-authentication-token-123456789";
    private readonly WebApplication _app;
    public AgentSessionService Sessions { get; }
    public ConcurrentQueue<RecordingChatClient> Clients { get; } = new();
    public HttpClient Http { get; }

    private ServerFixture(AgentSessionLimits? limits, AgentSessionService? sessions, bool allowCatalogRefresh, IPluginContent? plugins, ISkillText? skills, McpControlPorts? connectors, PromptCommandCatalog? commands)
    {
        Sessions = sessions ?? new(model =>
        {
            if (model != "test/chat") throw new KeyNotFoundException("Model not configured.");
            var client = new RecordingChatClient();
            Clients.Enqueue(client);
            return client;
        }, limits);
        var catalog = Substitute.For<IModelCatalog>();
        catalog.ListProviders().Returns([new CatalogProviderDto("test", "Test", null, [], "chat", null)]);
        catalog.List(Arg.Any<string?>()).Returns([]);
        _app = CodeAssistHttpServer.Build(new() { Token = Token, DefaultModel = "test/chat", AllowCatalogRefresh = allowCatalogRefresh, AllowedOrigins = ["http://localhost:3000"] }, Sessions, new(catalog),
            builder =>
            {
                builder.Logging.ClearProviders();
                if (commands is not null) builder.Services.AddSingleton(commands);
            }, plugins, skills, connectors);
        Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public static async Task<ServerFixture> StartAsync(AgentSessionLimits? limits = null, AgentSessionService? sessions = null, bool allowCatalogRefresh = true, IPluginContent? plugins = null, ISkillText? skills = null, McpControlPorts? connectors = null, PromptCommandCatalog? commands = null)
    {
        var fixture = new ServerFixture(limits, sessions, allowCatalogRefresh, plugins, skills, connectors, commands);
        try
        {
            await fixture._app.StartAsync(TestContext.Current.CancellationToken);
            fixture.Http.BaseAddress = new Uri(fixture._app.Urls.Single());
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public async Task<AgentSessionDto> CreateAsync()
    {
        using var response = await Http.PostAsJsonAsync("/v1/sessions", new CreateAgentSessionRequest(), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        var session = await response.Content.ReadFromJsonAsync<AgentSessionDto>(TestContext.Current.CancellationToken);
        session.ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"/v1/sessions/{session.Id}");
        return session;
    }

    public Task<HttpResponseMessage> SendAsync(Guid id, string text, bool stream = false) =>
        Http.PostAsJsonAsync($"/v1/sessions/{id}/messages{(stream ? "/stream" : "")}", new SendAgentMessageRequest(text), TestContext.Current.CancellationToken);

    public Task StopAsync() => _app.StopAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Sessions.Dispose();
        await _app.DisposeAsync();
        Http.Dispose();
    }
}