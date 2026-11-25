using System.Text.Json;
using CodeAssist.Server.Composition;
using CodeAssist.Models.Chat;
using CodeAssist.Contracts;
using CodeAssist.Configuration;
using CodeAssist.Persistence;
using CodeAssist.Sessions;
using CodeAssist.Testing;
using CodeAssist.Server.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Server.IntegrationTests.Http;

public sealed class ConfiguredProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_RecoverOnlyWhenEnabledAndUseInjectedCommandCatalog_When_RuntimeRestarts(bool disableRecovery)
    {
        using var sandbox = new SettingsSandbox();
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        var settings = Settings(upstream.Url!, "openai");
        settings.Host.RecoverConversations.ShouldBeTrue();
        settings.Host.Listen.Http.Token = ServerFixture.Token;
        settings.Agent.Model = "test/chat";
        settings.Commands["host-probe"] = new() { Template = "runtime wired {{args}}" };
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        var token = TestContext.Current.CancellationToken;
        AgentSessionDto? original = null;
        for (var generation = 0; generation < 2; generation++)
        {
            settings.Host.RecoverConversations = generation == 0 || !disableRecovery;
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = ServerRuntime.RunAsync(settings, sandbox.Locations, environment, endpoint => ready.TrySetResult(endpoint),
                address: "127.0.0.1:0", cancellation: shutdown.Token, configure: builder => builder.Logging.ClearProviders());
            try
            {
                if (await Task.WhenAny(ready.Task, running) == running) await running;
                using var http = new HttpClient { BaseAddress = new Uri(await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token)) };
                http.DefaultRequestHeaders.Authorization = new(System.Net.Http.Headers.AuthenticationHeaderValue.Parse("Bearer " + ServerFixture.Token).Scheme, ServerFixture.Token);
                var commands = (await http.GetFromJsonAsync<ConversationCommandDto[]>("/v1/commands", token))!;
                commands.Single(command => command.Name == "prompt/host-probe").Origin.ShouldBe("settings");
                using var expanded = await http.PostAsJsonAsync("/v1/commands/expand", new ExpandConversationCommandRequest("prompt/host-probe", "task"), token);
                expanded.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
                (await expanded.Content.ReadFromJsonAsync<ConversationCommandExpansion>(token))!.Text.ShouldContain("\n\nruntime wired task\n\n");
                if (generation == 0)
                {
                    using var created = await http.PostAsJsonAsync("/v1/sessions", new CreateAgentSessionRequest("test/chat"), token);
                    created.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
                    original = (await created.Content.ReadFromJsonAsync<AgentSessionDto>(token))!;
                    using var sent = await http.PostAsJsonAsync($"/v1/sessions/{original.Id}/messages", new SendAgentMessageRequest("durable seed"), token);
                    sent.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
                    using var archived = await http.PostAsJsonAsync($"/v1/sessions/{original.Id}/details", new UpdateSessionRequest("Persisted title", true), token);
                    archived.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
                }
                else if (disableRecovery)
                {
                    (await http.GetFromJsonAsync<AgentSessionDto[]>("/v1/sessions", token)).ShouldBeEmpty();
                    using var missing = await http.GetAsync($"/v1/sessions/{original!.Id}/details", token);
                    missing.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
                }
                else
                {
                    (await http.GetFromJsonAsync<AgentSessionDto[]>("/v1/sessions", token)).ShouldBe([original! with { MessageCount = 2 }]);
                    (await http.GetFromJsonAsync<AgentMessageDto[]>($"/v1/sessions/{original!.Id}/messages", token))!.Select(message => message.Text).ShouldBe(["durable seed", "OK"]);
                    (await http.GetFromJsonAsync<AgentSessionDetails>($"/v1/sessions/{original.Id}/details", token)).ShouldBe(new(original.Id, 3, "Persisted title", true, 1, 1, 1, 0, true));
                    using var blocked = await http.PostAsJsonAsync($"/v1/sessions/{original.Id}/messages", new SendAgentMessageRequest("blocked archived"), token);
                    blocked.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
                }
                upstream.LogEntries.Count().ShouldBe(1);
            }
            finally
            {
                await shutdown.CancelAsync();
                await running;
            }
        }
    }

    [Theory]
    [InlineData("turns", 0, false)]
    [InlineData("missing-usage", 0, false)]
    [InlineData("reported", 9, true)]
    [InlineData("reported", 10, false)]
    [InlineData("reported", 11, false)]
    public async Task Should_ApplyConfiguredTurnAndTokenLimits_When_CompositionCreatesSession(string partition, int tokens, bool admitSecond)
    {
        using var upstream = WireMockServer.Start();
        var stream = OpenAiStream.Replace(OpenAiUsageLine, partition == "reported"
            ? "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"upstream/model\",\"choices\":[],\"usage\":{\"prompt_tokens\":" + (tokens - 2) + ",\"completion_tokens\":2,\"total_tokens\":" + tokens + "}}"
            : partition == "missing-usage" ? "" : OpenAiUsageLine, StringComparison.Ordinal);
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(stream));
        var settings = Settings(upstream.Url!, "openai");
        settings.Agent.MaxTurns = partition == "turns" ? 1 : 8;
        settings.Agent.MaxTokens = 10;
        using var sessions = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" });
        var token = TestContext.Current.CancellationToken;
        var session = await sessions.CreateAsync("test/chat", token);
        (await sessions.SendAsync(session.Id, "first", cancellationToken: token)).Text.ShouldBe("OK");
        if (partition == "reported") sessions.Details(session.Id).ShouldBe(new(session.Id, 0, "Untitled conversation", false, tokens - 2, 2, 1, 0, true));
        else sessions.Details(session.Id).TokensReported.ShouldBe(partition == "turns");
        if (admitSecond) (await sessions.SendAsync(session.Id, "second", cancellationToken: token)).Text.ShouldBe("OK");
        else await Should.ThrowAsync<SessionConflictException>(() => sessions.SendAsync(session.Id, "blocked", cancellationToken: token));
        upstream.LogEntries.Count().ShouldBe(admitSecond ? 2 : 1);
        sessions.Messages(session.Id).Select(message => message.Text).ShouldBe(admitSecond ? ["first", "OK", "second", "OK"] : ["first", "OK"]);
    }

    [Fact]
    public async Task Should_ApplyCurrentPolicyWithStableIdAndSdkHistory_When_DurableSessionRecovers()
    {
        using var sandbox = new SettingsSandbox();
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        var settings = Settings(upstream.Url!, "openai");
        settings.Roles[settings.Agent.Role].Prompt = "OLD_RECOVERY_INSTRUCTIONS";
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        var directory = Path.Combine(sandbox.Root, "conversations");
        var token = TestContext.Current.CancellationToken;
        AgentSessionDto original;
        using (var store = new FileConversationStore(directory))
        using (var source = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment, locations: sandbox.Locations))
        {
            await source.EnablePersistenceAsync(store, token);
            original = await source.CreateAsync("test/chat", token);
            await source.SendAsync(original.Id, "before shutdown", cancellationToken: token);
        }
        settings.Roles[settings.Agent.Role].Prompt = "CURRENT_RECOVERY_INSTRUCTIONS";
        settings.Tools.Disabled.Add("skill.load");
        using var restartedStore = new FileConversationStore(directory);
        using var restarted = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment, locations: sandbox.Locations);
        await restarted.EnablePersistenceAsync(restartedStore, token);
        restarted.Get(original.Id).ShouldBe(original with { MessageCount = 2 });
        restarted.Messages(original.Id).Select(message => message.Text).ShouldBe(["before shutdown", "OK"]);
        upstream.LogEntries.Count().ShouldBe(1);
        (await restarted.SendAsync(original.Id, "after restart", cancellationToken: token)).Text.ShouldBe("OK");
        var requests = upstream.LogEntries.Select(entry => JsonSerializer.Deserialize<JsonElement>(entry.RequestMessage!.Body!)).ToArray();
        requests[0].GetProperty("tools").GetArrayLength().ShouldBeGreaterThan(0);
        requests[1].GetRawText().ShouldContain("CURRENT_RECOVERY_INSTRUCTIONS");
        requests[1].GetRawText().ShouldNotContain("OLD_RECOVERY_INSTRUCTIONS");
        (requests[1].TryGetProperty("tools", out var tools) ? tools.GetArrayLength() : 0).ShouldBe(0);
        requests[1].GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() is "user" or "assistant")
            .Select(message => (message.GetProperty("role").GetString(), message.GetProperty("content").GetString())).ShouldBe([("user", "before shutdown"), ("assistant", "OK"), ("user", "after restart")]);
        restarted.Details(original.Id).Revision.ShouldBe(3);
    }

    [Fact]
    public async Task Should_PreserveUnavailableSessionsAndRecoverConfiguredSessions_When_SavedProviderIsRemoved()
    {
        using var sandbox = new SettingsSandbox();
        var settings = Settings("http://127.0.0.1:1", "openai");
        var provider = settings.Providers["test"];
        settings.Providers["available"] = provider;
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        using var store = new FileConversationStore(Path.Combine(sandbox.Root, "conversations"));
        var token = TestContext.Current.CancellationToken;
        AgentSessionDto unavailable;
        AgentSessionDto available;
        using (var source = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment))
        {
            await source.EnablePersistenceAsync(store, token);
            unavailable = await source.CreateAsync("test/chat", token);
            available = await source.CreateAsync("available/chat", token);
        }
        var saved = await store.ListAsync(token);
        settings.Providers.Remove("test");
        AgentSessionDto created;
        using (var restarted = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment))
        {
            await restarted.EnablePersistenceAsync(store, token);
            restarted.List().ShouldBe([available]);
            Should.Throw<KeyNotFoundException>(() => restarted.Get(unavailable.Id));
            JsonSerializer.Serialize(await store.ListAsync(token)).ShouldBe(JsonSerializer.Serialize(saved));
            created = await restarted.CreateAsync("available/chat", token);
        }
        settings.Providers["test"] = provider;
        using var restored = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);
        await restored.EnablePersistenceAsync(store, token);
        restored.Get(unavailable.Id).ShouldBe(unavailable);
        restored.Get(available.Id).ShouldBe(available);
        restored.Get(created.Id).ShouldBe(created);
    }

    [Fact]
    public async Task Should_RejectRecoveryWithoutPublishingSessions_When_CurrentProviderPolicyDisallowsArchive()
    {
        using var sandbox = new SettingsSandbox();
        var settings = Settings("http://127.0.0.1:1", "openai");
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        using var store = new FileConversationStore(Path.Combine(sandbox.Root, "conversations"));
        var token = TestContext.Current.CancellationToken;
        AgentSessionDto original;
        using (var source = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment))
        {
            await source.EnablePersistenceAsync(store, token);
            original = await source.CreateAsync("test/chat", token);
        }
        settings.Policy.Rules.Add(new() { Action = "provider.use", Resource = "test", Effect = RuleDecision.Deny });
        using var restarted = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);
        await Should.ThrowAsync<NotSupportedException>(() => restarted.EnablePersistenceAsync(store, token));
        restarted.List().ShouldBeEmpty();
        (await store.ListAsync(token)).ShouldHaveSingleItem().Revision.ShouldBe(1);
        settings.Policy.Rules.Clear();
        await restarted.EnablePersistenceAsync(store, token);
        restarted.Get(original.Id).ShouldBe(original);
    }

    [Theory]
    [InlineData("openai", false)]
    [InlineData("openai", true)]
    [InlineData("anthropic", false)]
    [InlineData("anthropic", true)]
    public async Task Should_PreserveModelContextAndIsolateContinuation_When_SessionIsTransferred(string protocol, bool fork)
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream")
            .WithBody(protocol == "anthropic" ? AnthropicStream : OpenAiStream));
        var settings = Settings(upstream.Url!, protocol);
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        using var source = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);
        using var destination = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);
        var token = TestContext.Current.CancellationToken;
        var original = await source.CreateAsync("test/chat", token);
        (await source.SendAsync(original.Id, "committed seed", cancellationToken: token)).Text.ShouldBe("OK");
        var archive = await source.ExportAsync(original.Id, token);
        archive.Version.ShouldBe(1);
        archive.Model.ShouldBe("test/chat");
        archive.CreatedAt.ShouldBe(original.CreatedAt);
        archive.Messages.Select(message => (message.Sequence, message.Role, message.Text))
            .ShouldBe([(1, "user", "committed seed"), (2, "assistant", "OK")]);
        destination.List().ShouldBeEmpty();
        Should.Throw<KeyNotFoundException>(() => destination.Get(original.Id));
        var owner = fork ? source : destination;
        var branch = fork ? await source.ForkAsync(original.Id, token) : await destination.ImportAsync(archive, token);
        branch.Id.ShouldNotBe(original.Id);
        branch.Model.ShouldBe("test/chat");
        branch.Running.ShouldBeFalse();
        branch.MessageCount.ShouldBe(2);
        owner.Messages(branch.Id).ShouldBe(archive.Messages);
        (await owner.SendAsync(branch.Id, "branch only", cancellationToken: token)).Text.ShouldBe("OK");
        (await source.SendAsync(original.Id, "source only", cancellationToken: token)).Text.ShouldBe("OK");
        owner.Messages(branch.Id).Select(message => message.Text).ShouldBe(["committed seed", "OK", "branch only", "OK"]);
        source.Messages(original.Id).Select(message => message.Text).ShouldBe(["committed seed", "OK", "source only", "OK"]);
        var requests = upstream.LogEntries.Select(entry => JsonSerializer.Deserialize<JsonElement>(entry.RequestMessage!.Body!)).ToArray();
        requests.Length.ShouldBe(3);
        foreach (var (request, continuation) in new[] { (requests[1], "branch only"), (requests[2], "source only") })
        {
            request.GetProperty("model").GetString().ShouldBe("upstream/model");
            var messages = request.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() is "user" or "assistant").ToArray();
            messages.Select(message => message.GetProperty("role").GetString()).ShouldBe(["user", "assistant", "user"]);
            messages.Select(message => message.GetProperty("content").ValueKind == JsonValueKind.String
                ? message.GetProperty("content").GetString()
                : string.Concat(message.GetProperty("content").EnumerateArray().Select(content => content.GetProperty("text").GetString())))
                .ShouldBe(["committed seed", "OK", continuation]);
        }
    }

    [Theory]
    [InlineData("missing-provider")]
    [InlineData("denied-provider")]
    public async Task Should_RejectTransferredModelAndReleaseSlot_When_CurrentConfigurationDisallowsIt(string restriction)
    {
        using var upstream = WireMockServer.Start();
        var settings = Settings(upstream.Url!, "openai");
        settings.Host.MaxActiveSessions = 1;
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        using var source = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);
        var original = await source.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        var archive = await source.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        var provider = settings.Providers["test"];
        if (restriction == "missing-provider") settings.Providers.Remove("test");
        else settings.Policy.Rules.Add(new() { Action = "provider.use", Resource = "test", Effect = RuleDecision.Deny });
        using var destination = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);

        if (restriction == "missing-provider")
            await Should.ThrowAsync<KeyNotFoundException>(() => destination.ImportAsync(archive, TestContext.Current.CancellationToken));
        else
            await Should.ThrowAsync<NotSupportedException>(() => destination.ImportAsync(archive, TestContext.Current.CancellationToken));

        destination.List().ShouldBeEmpty();
        upstream.LogEntries.ShouldBeEmpty();
        await using var server = await ServerFixture.StartAsync(sessions: destination);
        using var rejected = await server.Http.PostAsJsonAsync("/v1/sessions/import", archive, TestContext.Current.CancellationToken);
        rejected.StatusCode.ShouldBe(restriction == "missing-provider" ? System.Net.HttpStatusCode.NotFound : System.Net.HttpStatusCode.UnprocessableEntity);
        destination.List().ShouldBeEmpty();
        settings.Providers["test"] = provider;
        settings.Policy.Rules.Clear();
        var admitted = await destination.ImportAsync(archive, TestContext.Current.CancellationToken);
        destination.List().Select(session => session.Id).ShouldBe([admitted.Id]);
    }

    [Fact]
    public async Task Should_RequireExplicitSerializedTransfer_When_ServiceIsRecreated()
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        var settings = Settings(upstream.Url!, "openai");
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        byte[] transfer;
        Guid originalId;
        using (var original = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment))
        {
            var session = await original.CreateAsync("test/chat", TestContext.Current.CancellationToken);
            originalId = session.Id;
            await original.SendAsync(session.Id, "before shutdown", cancellationToken: TestContext.Current.CancellationToken);
            transfer = JsonSerializer.SerializeToUtf8Bytes(await original.ExportAsync(session.Id, TestContext.Current.CancellationToken), JsonSerializerOptions.Web);
        }
        using var restarted = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment);
        restarted.List().ShouldBeEmpty();
        Should.Throw<KeyNotFoundException>(() => restarted.Get(originalId));
        await using var server = await ServerFixture.StartAsync(sessions: restarted);
        using var response = await server.Http.PostAsJsonAsync("/v1/sessions/import",
            JsonSerializer.Deserialize<AgentSessionArchive>(transfer, JsonSerializerOptions.Web), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        var imported = (await response.Content.ReadFromJsonAsync<AgentSessionDto>(TestContext.Current.CancellationToken))!;
        imported.Id.ShouldNotBe(originalId);
        restarted.Messages(imported.Id).Select(message => message.Text).ShouldBe(["before shutdown", "OK"]);
        using var continued = await server.SendAsync(imported.Id, "after restart");
        continued.EnsureSuccessStatusCode();
        using var exported = await server.Http.GetAsync($"/v1/sessions/{imported.Id}/archive", TestContext.Current.CancellationToken);
        exported.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var archive = (await exported.Content.ReadFromJsonAsync<AgentSessionArchive>(TestContext.Current.CancellationToken))!;
        archive.Messages.Select(message => message.Text).ShouldBe(["before shutdown", "OK", "after restart", "OK"]);
        using var request = JsonDocument.Parse(upstream.LogEntries.Last().RequestMessage!.Body!);
        request.RootElement.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "user")
            .Select(message => message.GetProperty("content").GetString()).ShouldBe(["before shutdown", "after restart"]);
    }

    [Fact]
    public async Task Should_RejectOversizedExportAndForkWithoutChangingHistory_When_CommittedSessionExceedsTransferBudget()
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        using var sessions = AgentSessionComposition.CreateAgentSessions(Settings(upstream.Url!, "openai"), ProviderRegistry.CreateDefault(),
            new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" });
        var session = await sessions.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        var prompt = new string('x', 65536);
        for (var turn = 0; turn < 3; turn++)
            (await sessions.SendAsync(session.Id, prompt, cancellationToken: TestContext.Current.CancellationToken)).Text.ShouldBe("OK");

        var exported = await Should.ThrowAsync<ArgumentException>(() => sessions.ExportAsync(session.Id, TestContext.Current.CancellationToken));
        var forked = await Should.ThrowAsync<ArgumentException>(() => sessions.ForkAsync(session.Id, TestContext.Current.CancellationToken));

        exported.Message.ShouldContain("384 KiB");
        forked.Message.ShouldContain("384 KiB");
        sessions.Messages(session.Id).Select(message => message.Text).ShouldBe([prompt, "OK", prompt, "OK", prompt, "OK"]);
        sessions.List().Select(item => item.Id).ShouldBe([session.Id]);
        sessions.Get(session.Id).Running.ShouldBeFalse();
        upstream.LogEntries.Count().ShouldBe(3);
    }

    [Fact]
    public async Task Should_RejectActiveTransferAndPreserveCommittedContext_When_PartialTurnIsCanceled()
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        using var sessions = AgentSessionComposition.CreateAgentSessions(Settings(upstream.Url!, "openai"), ProviderRegistry.CreateDefault(),
            new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" });
        await using var server = await ServerFixture.StartAsync(sessions: sessions);
        var token = TestContext.Current.CancellationToken;
        var original = await sessions.CreateAsync("test/chat", token);
        await sessions.SendAsync(original.Id, "committed", cancellationToken: token);
        var before = await sessions.ExportAsync(original.Id, token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = sessions.SendAsync(original.Id, "aborted", async (item, cancellation) =>
        {
            if (item.Kind != "text.delta") return;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
        }, token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            sessions.Get(original.Id).Running.ShouldBeTrue();
            await Should.ThrowAsync<CodeAssist.Sessions.SessionConflictException>(() => sessions.ExportAsync(original.Id, token));
            await Should.ThrowAsync<CodeAssist.Sessions.SessionConflictException>(() => sessions.ForkAsync(original.Id, token));
            using var exported = await server.Http.GetAsync($"/v1/sessions/{original.Id}/archive", token);
            using var forked = await server.Http.PostAsync($"/v1/sessions/{original.Id}/fork", null, token);
            exported.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
            forked.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
            sessions.List().Select(session => session.Id).ShouldBe([original.Id]);
        }
        finally
        {
            await sessions.CancelAsync(original.Id);
            await Should.ThrowAsync<OperationCanceledException>(() => pending);
        }
        sessions.Get(original.Id).Running.ShouldBeFalse();
        sessions.Messages(original.Id).ShouldBe(before.Messages);
        var after = await sessions.ExportAsync(original.Id, token);
        after.State.GetRawText().ShouldBe(before.State.GetRawText());
        var branch = await sessions.ForkAsync(original.Id, token);
        (await sessions.SendAsync(branch.Id, "resumed", cancellationToken: token)).Text.ShouldBe("OK");
        var request = upstream.LogEntries.Last().RequestMessage!.Body!;
        using var body = JsonDocument.Parse(request);
        body.RootElement.GetProperty("messages").EnumerateArray().Where(message => message.GetProperty("role").GetString() == "user")
            .Select(message => message.GetProperty("content").GetString()).ShouldBe(["committed", "resumed"]);
        sessions.Messages(original.Id).Select(message => message.Text).ShouldBe(["committed", "OK"]);
    }

    [Fact]
    public async Task Should_ApplyCurrentInstructionsAndToolPolicy_When_ArchiveIsImported()
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        var settings = Settings(upstream.Url!, "openai");
        settings.Roles[settings.Agent.Role].Prompt = "OLD_ROLE_SENTINEL";
        var root = Path.Combine(Path.GetTempPath(), "ca-transfer-policy-" + Guid.NewGuid().ToString("N"));
        var locations = new SettingsLocations(Path.Combine(root, "home"), root, Path.Combine(root, "managed"));
        var environment = new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" };
        using var source = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment, locations: locations);
        var original = await source.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        await source.SendAsync(original.Id, "seed", cancellationToken: TestContext.Current.CancellationToken);
        var archive = await source.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        settings.Roles[settings.Agent.Role].Prompt = "CURRENT_ROLE_SENTINEL";
        settings.Tools.Disabled.Add("skill.load");
        using var destination = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), environment, locations: locations);

        var imported = await destination.ImportAsync(archive, TestContext.Current.CancellationToken);
        (await destination.SendAsync(imported.Id, "continue", cancellationToken: TestContext.Current.CancellationToken)).Text.ShouldBe("OK");

        var requests = upstream.LogEntries.Select(entry => JsonSerializer.Deserialize<JsonElement>(entry.RequestMessage!.Body!)).ToArray();
        requests.Length.ShouldBe(2);
        requests[0].GetProperty("tools").GetArrayLength().ShouldBeGreaterThan(0);
        requests[1].GetRawText().ShouldContain("CURRENT_ROLE_SENTINEL");
        requests[1].GetRawText().ShouldNotContain("OLD_ROLE_SENTINEL");
        (requests[1].TryGetProperty("tools", out var tools) ? tools.GetArrayLength() : 0).ShouldBe(0);
        destination.Messages(imported.Id).Select(message => message.Text).ShouldBe(["seed", "OK", "continue", "OK"]);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task Should_RunConfiguredSdkThroughFrameworkAndHttp_When_ModelIsSelected(string protocol)
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream")
            .WithBody(protocol == "anthropic" ? AnthropicStream : OpenAiStream));
        var settings = Settings(upstream.Url!, protocol);
        using var sessions = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(),
            new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" });
        await using var server = await ServerFixture.StartAsync(sessions: sessions);
        var session = await server.CreateAsync();

        using var response = await server.SendAsync(session.Id, "hello");
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<AgentMessageDto>(TestContext.Current.CancellationToken);
        reply!.Text.ShouldBe("OK");
        var request = upstream.LogEntries.ShouldHaveSingleItem()!.RequestMessage.ShouldNotBeNull();
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("model").GetString().ShouldBe("upstream/model");
        body.RootElement.GetProperty("stream").GetBoolean().ShouldBeTrue();
        var maximum = body.RootElement.TryGetProperty("max_tokens", out var tokens) ? tokens : body.RootElement.GetProperty("max_completion_tokens");
        maximum.GetInt32().ShouldBe(64);
        upstream.FindLogEntries(protocol == "anthropic"
            ? Request.Create().WithHeader("x-api-key", "fixture-provider-key")
            : Request.Create().WithHeader("Authorization", "Bearer fixture-provider-key")).ShouldHaveSingleItem();
        server.Sessions.Messages(session.Id).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Should_RejectDeniedProviderBeforeNetwork_When_ManagedPolicyRestrictsIt()
    {
        var settings = Settings("http://127.0.0.1:1", "openai");
        settings.Policy.Rules.Add(new() { Action = "provider.use", Resource = "te*", Effect = RuleDecision.Deny });
        using var sessions = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(), new Dictionary<string, string>());
        await Should.ThrowAsync<NotSupportedException>(() => sessions.CreateAsync("test/chat", TestContext.Current.CancellationToken));
        sessions.List().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("disabled", false)]
    [InlineData("role-deny", false)]
    [InlineData("policy-deny", false)]
    [InlineData("policy-ask", false)]
    [InlineData("model-no-tools", false)]
    public async Task Should_AdvertiseOnlyAdmittedSkillFunctions_When_ConversationUsesConfiguredProvider(string restriction, bool expected)
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/*").UsingPost()).RespondWith(Response.Create()
            .WithStatusCode(200).WithHeader("Content-Type", "text/event-stream").WithBody(OpenAiStream));
        var settings = Settings(upstream.Url!, "openai");
        if (restriction == "disabled") settings.Tools.Disabled.Add("skill.load");
        if (restriction == "model-no-tools") settings.Providers["test"].Models["chat"].Tools = false;
        if (restriction == "role-deny") settings.Roles[settings.Agent.Role].Tools.Deny.Add("read");
        if (restriction.StartsWith("policy-", StringComparison.Ordinal)) settings.Policy.Rules.Add(new()
        {
            Action = "tool.use", Resource = "skill.*", Effect = restriction == "policy-ask" ? RuleDecision.Ask : RuleDecision.Deny,
        });
        var root = Path.Combine(Path.GetTempPath(), "ca-skill-admission-" + Guid.NewGuid().ToString("N"));
        using var sessions = AgentSessionComposition.CreateAgentSessions(settings, ProviderRegistry.CreateDefault(),
            new Dictionary<string, string> { ["FIXTURE_KEY"] = "fixture-provider-key" },
            locations: new(Path.Combine(root, "home"), root, Path.Combine(root, "managed")));
        var session = await sessions.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        (await sessions.SendAsync(session.Id, "Hello", cancellationToken: TestContext.Current.CancellationToken)).Text.ShouldBe("OK");
        var request = upstream.LogEntries.ShouldHaveSingleItem()!.RequestMessage.ShouldNotBeNull();
        using var body = JsonDocument.Parse(request.Body!);
        var hasTools = body.RootElement.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0;
        hasTools.ShouldBe(expected);
        if (expected) tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString())
            .ShouldBe(["skill_list", "skill_load", "skill_resource"]);
    }

    private static CodeAssistSettings Settings(string endpoint, string protocol) => new()
    {
        Providers = new()
        {
            ["test"] = new()
            {
                Protocol = protocol == "anthropic" ? ProviderProtocol.Anthropic : ProviderProtocol.OpenAi,
                Api = ProviderApi.Chat,
                Endpoint = endpoint + "/v1/",
                ApiKey = "{env:FIXTURE_KEY}",
                Models = new() { ["chat"] = new() { ModelId = "upstream/model", MaxOutputTokens = 64 } },
            },
        },
    };

    private const string OpenAiUsageLine = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"upstream/model\",\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    private const string OpenAiStream = """
        data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"upstream/model","choices":[{"index":0,"delta":{"role":"assistant","content":"OK"},"finish_reason":null}]}

        data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"upstream/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"upstream/model","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}

        data: [DONE]


        """;

    private const string AnthropicStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"upstream/model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;
}