using System.Text.Json;
using CodeAssist.Models.Chat;
using CodeAssist.Server.Composition;
using CodeAssist.Configuration;
using CodeAssist.Sessions;
using Microsoft.Extensions.AI;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Models.IntegrationTests.Chat;

public sealed class HostedConnectorTests
{
    [Fact]
    public async Task Should_StreamAndReplayHostedResults_When_AdmittedSessionContinues()
    {
        using var server = WireMockServer.Start();
        var frames = new[]
        {
            ("message_start", """{"type":"message_start","message":{"id":"fixture-message","type":"message","role":"assistant","model":"fixture-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"mcp_tool_use","id":"fixture-call","name":"lookup","server_name":"inventory","input":{}}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"id\":\"one\"}"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"mcp_tool_result","tool_use_id":"fixture-call","is_error":false,"content":[{"type":"text","text":"fixture remote result"}]}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            ("content_block_start", """{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Done"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":2}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}"""),
            ("message_stop", """{"type":"message_stop"}"""),
        };
        server.Given(Request.Create().WithPath("/v1/messages").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "text/event-stream")
            .WithBody(string.Concat(frames.Select(frame => $"event: {frame.Item1}\ndata: {frame.Item2}\n\n"))));
        var settings = new CodeAssistSettings();
        settings.Providers["fixture"] = new() { Protocol = ProviderProtocol.Anthropic, Endpoint = server.Url!, RemoteTools = new() { Enabled = true, AcceptRemoteExecution = true,
            Servers = new() { ["inventory"] = new() { Url = "https://inventory.example.test/mcp" } } } };
        using var sessions = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string>());
        var session = await sessions.CreateAsync("fixture/fixture-model", TestContext.Current.CancellationToken);
        var events = new List<string>();
        (await sessions.SendAsync(session.Id, "Look up one", (update, _) => { events.Add(update.Kind); return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken)).Text.ShouldBe("Done");
        events.ShouldContain("hosted.call");
        events.ShouldContain("hosted.result");
        await sessions.SendAsync(session.Id, "Continue", cancellationToken: TestContext.Current.CancellationToken);
        var body = server.LogEntries.Where(entry => entry.RequestMessage?.Path == "/v1/messages").Select(entry => JsonSerializer.Deserialize<JsonElement>(entry.RequestMessage!.Body!))
            .Single(document => document.GetProperty("messages").GetArrayLength() > 1);
        body.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "assistant")
            .SelectMany(message => message.GetProperty("content").EnumerateArray()).Any(block => block.GetProperty("type").GetString() == "mcp_tool_result").ShouldBeTrue();
    }

    [Fact]
    public async Task Should_ApplyAdmittedHostedControlsWithoutDroppingLocalTools_When_RemoteExecutionIsAcknowledged()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/v1/messages").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBody("""{"id":"fixture-message","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"Done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""));
        var settings = new ProviderSettings { Protocol = ProviderProtocol.Anthropic, Endpoint = server.Url!, RemoteTools = new() { Enabled = true, AcceptRemoteExecution = true } };
        settings.RemoteTools.Servers["inventory"] = new() { Url = "https://inventory.example.test/mcp", Token = "{env:FIXTURE_TOKEN}", DefaultEnabled = false, Deferred = true,
            ToolRules = new() { ["lookup"] = new() { Enabled = true, Deferred = false }, ["delete"] = new() { Enabled = false } }, CacheSeconds = 300 };
        var environment = new Dictionary<string, string> { ["FIXTURE_TOKEN"] = "fixture-connector-token" };
        Should.Throw<UnauthorizedAccessException>(() => ProviderComposition.CreateChatClient("anthropic", "fixture-model", settings, ProviderRegistry.CreateDefault(), environment));
        using var client = ProviderComposition.CreateChatClient("anthropic", "fixture-model", settings, ProviderRegistry.CreateDefault(), environment, admitHosted: _ => true);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Look up one")], new ChatOptions { Tools = [AIFunctionFactory.Create(() => "local", "local_lookup")] }, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/v1/messages").RequestMessage!.Body!);
        body.RootElement.GetProperty("mcp_servers")[0].GetProperty("authorization_token").GetString().ShouldBe("fixture-connector-token");
        var tools = body.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        tools.Any(tool => tool.TryGetProperty("name", out var name) && name.GetString() == "local_lookup").ShouldBeTrue();
        var remote = tools.Single(tool => tool.TryGetProperty("type", out var type) && type.GetString() == "mcp_toolset");
        remote.GetProperty("default_config").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        remote.GetProperty("configs").GetProperty("lookup").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        settings.RemoteTools.AcceptRemoteExecution = false;
        Should.Throw<UnauthorizedAccessException>(() => ProviderComposition.CreateChatClient("anthropic", "fixture-model", settings, ProviderRegistry.CreateDefault(), environment, admitHosted: _ => true));
    }

    [Fact]
    public async Task Should_PreserveHostedConnectorBlocks_When_OfficialAdapterExecutesRemoteTools()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/v1/messages").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBody("""{"id":"fixture-message","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"mcp_tool_use","id":"fixture-call","name":"lookup","server_name":"inventory","input":{"id":"one"}},{"type":"mcp_tool_result","tool_use_id":"fixture-call","is_error":false,"content":[{"type":"text","text":"fixture remote result"}]},{"type":"text","text":"Done"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""));
        using var client = ProviderRegistry.CreateDefault().CreateChatClient(new ProviderConnection { Id = "anthropic", Endpoint = new Uri(server.Url!), ApiKey = "fixture-provider-key", HostedExecutionEnabled = true }, "fixture-model");
        var tool = new HostedConnectorTool("inventory", new Uri("https://inventory.example.test/mcp"))
        {
            AllowedTools = ["lookup"], Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer fixture-connector-token" },
        };
        var result = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Look up one")], new ChatOptions { Tools = [tool] }, TestContext.Current.CancellationToken);
        result.Messages.SelectMany(message => message.Contents).ShouldContain(content => content is McpServerToolCallContent);
        result.Messages.SelectMany(message => message.Contents).ShouldContain(content => content is McpServerToolResultContent);
        using var body = JsonDocument.Parse(server.LogEntries.Single(entry => entry.RequestMessage?.Path == "/v1/messages").RequestMessage!.Body!);
        body.RootElement.GetProperty("mcp_servers")[0].GetProperty("name").GetString().ShouldBe("inventory");
    }
}