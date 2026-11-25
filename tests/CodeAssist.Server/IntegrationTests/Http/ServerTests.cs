using CodeAssist.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Server.Http;
using CodeAssist.Persistence;
using CodeAssist.Testing;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Server.IntegrationTests.Http;

public sealed class ServerTests
{
    [Theory]
    [InlineData("failure")]
    [InlineData("canceled")]
    public async Task Should_NotRefundFailedTurnBudgetAcrossRestartOrRewind_When_AllowedTurnsAreConsumed(string outcome)
    {
        using var sandbox = new SettingsSandbox();
        var directory = Path.Combine(sandbox.Root, "conversations");
        var token = TestContext.Current.CancellationToken;
        AgentSessionDto original;
        using (var store = new FileConversationStore(directory))
        using (var sessions = new AgentSessionService(_ => new RecordingChatClient { ReportedUsage = new() { InputTokenCount = 3, OutputTokenCount = 2 } }, new() { MaxSessionTurns = 2, MaxSessionTokens = 50 }))
        {
            await sessions.EnablePersistenceAsync(store, token);
            original = await sessions.CreateAsync("test/chat", token);
            await sessions.SendAsync(original.Id, "seed", cancellationToken: token);
            if (outcome == "failure") await Should.ThrowAsync<HttpRequestException>(() => sessions.SendAsync(original.Id, "fail", cancellationToken: token));
            else
            {
                using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
                await Should.ThrowAsync<OperationCanceledException>(() => sessions.SendAsync(original.Id, "cancel", (item, _) =>
                {
                    if (item.Kind == "text.delta") canceled.Cancel();
                    return ValueTask.CompletedTask;
                }, canceled.Token));
            }
        }
        using var restartedStore = new FileConversationStore(directory);
        var client = new RecordingChatClient();
        using var restarted = new AgentSessionService(_ => client, new() { MaxSessionTurns = 2, MaxSessionTokens = 50 });
        await restarted.EnablePersistenceAsync(restartedStore, token);
        var expected = new AgentSessionDetails(original.Id, 3, "Untitled conversation", false, 6, 4, 1, 1, true);
        restarted.Details(original.Id).ShouldBe(expected);
        await Should.ThrowAsync<SessionConflictException>(() => restarted.SendAsync(original.Id, "blocked before rewind", cancellationToken: token));
        restarted.Messages(original.Id).Select(message => message.Text).ShouldBe(["seed", "reply:seed"]);
        await restarted.RewindAsync(original.Id, 1, 3, token);
        restarted.Details(original.Id).ShouldBe(expected with { Revision = 4 });
        restarted.Messages(original.Id).ShouldBeEmpty();
        await Should.ThrowAsync<SessionConflictException>(() => restarted.SendAsync(original.Id, "blocked after rewind", cancellationToken: token));
        client.UserMessages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("update")]
    [InlineData("rewind")]
    public async Task Should_PreserveRevisionMetadataAndHistory_When_DurableEditIsAlreadyCanceled(string operation)
    {
        using var sandbox = new SettingsSandbox();
        using var store = new FileConversationStore(Path.Combine(sandbox.Root, "conversations"));
        using var sessions = new AgentSessionService(_ => new RecordingChatClient());
        var token = TestContext.Current.CancellationToken;
        await sessions.EnablePersistenceAsync(store, token);
        var session = await sessions.CreateAsync("test/chat", token);
        await sessions.SendAsync(session.Id, "seed", cancellationToken: token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => operation == "update"
            ? (Task)sessions.UpdateAsync(session.Id, new("Rejected", true), canceled.Token)
            : sessions.RewindAsync(session.Id, 1, 2, canceled.Token));
        sessions.Details(session.Id).ShouldBe(new(session.Id, 2, "Untitled conversation", false, 0, 0, 1, 0, false));
        sessions.Messages(session.Id).Select(message => message.Text).ShouldBe(["seed", "reply:seed"]);
        (await sessions.UpdateAsync(session.Id, new("Accepted", false), token)).Revision.ShouldBe(3);
        (await sessions.CheckpointsAsync(session.Id, token)).ShouldBe([3L, 2L, 1L]);
    }

    [Fact]
    public async Task Should_ExposeMetadataArchiveAndCheckpointShapesWithoutInference_When_DurableRoutesAreCalled()
    {
        using var sandbox = new SettingsSandbox();
        using var store = new FileConversationStore(Path.Combine(sandbox.Root, "conversations"));
        var client = new RecordingChatClient();
        using var sessions = new AgentSessionService(_ => client);
        var token = TestContext.Current.CancellationToken;
        await sessions.EnablePersistenceAsync(store, token);
        await using var server = await ServerFixture.StartAsync(sessions: sessions);
        var session = await server.CreateAsync();
        var path = $"/v1/sessions/{session.Id}";
        using var renamed = await server.Http.PostAsJsonAsync(path + "/details", new UpdateSessionRequest("[bold]Saved title[/]", true), token);
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var expected = new AgentSessionDetails(session.Id, 2, "[bold]Saved title[/]", true, 0, 0, 0, 0, false);
        (await renamed.Content.ReadFromJsonAsync<AgentSessionDetails>(token)).ShouldBe(expected);
        using var details = await server.Http.GetAsync(path + "/details", token);
        details.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await details.Content.ReadFromJsonAsync<JsonElement>(token);
        raw.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(new[] { "archived", "completedTurns", "failedTurns", "id", "inputTokens", "outputTokens", "revision", "title", "tokensReported" });
        raw.Deserialize<AgentSessionDetails>(JsonSerializerOptions.Web).ShouldBe(expected);
        using var blocked = await server.SendAsync(session.Id, "blocked");
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        sessions.Messages(session.Id).ShouldBeEmpty();
        client.UserMessages.ShouldBeEmpty();
        using var checkpoints = await server.Http.GetAsync(path + "/checkpoints", token);
        checkpoints.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await checkpoints.Content.ReadFromJsonAsync<long[]>(token)).ShouldBe([2L, 1L]);
        using var restored = await server.Http.PostAsJsonAsync(path + "/details", new UpdateSessionRequest("Restored", false), token);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await restored.Content.ReadFromJsonAsync<AgentSessionDetails>(token)).ShouldBe(expected with { Revision = 3, Title = "Restored", Archived = false });
        using var rewound = await server.Http.PostAsJsonAsync(path + "/rewind", new RewindSessionRequest(1, 3), token);
        rewound.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await rewound.Content.ReadFromJsonAsync<AgentSessionDto>(token)).ShouldBe(session);
        sessions.Details(session.Id).ShouldBe(expected with { Revision = 4, Title = "Restored", Archived = false });
        client.UserMessages.ShouldBeEmpty();
        using var admitted = await server.SendAsync(session.Id, "allowed");
        admitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        sessions.Messages(session.Id).Select(message => message.Text).ShouldBe(["allowed", "reply:allowed"]);
    }

    [Theory]
    [InlineData("details-get", false)]
    [InlineData("details-get", true)]
    [InlineData("details-post", false)]
    [InlineData("details-post", true)]
    [InlineData("checkpoints", false)]
    [InlineData("checkpoints", true)]
    [InlineData("rewind", false)]
    [InlineData("rewind", true)]
    public async Task Should_AuthenticateBeforeMetadataMutation_When_SessionRouteTokenIsAbsentOrWrong(string route, bool wrong)
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        server.Http.DefaultRequestHeaders.Authorization = wrong ? new AuthenticationHeaderValue("Bearer", new string('w', 40)) : null;
        var path = $"/v1/sessions/{session.Id}";
        var token = TestContext.Current.CancellationToken;
        using var response = route switch
        {
            "details-post" => await server.Http.PostAsJsonAsync(path + "/details", new UpdateSessionRequest("Forbidden", true), token),
            "rewind" => await server.Http.PostAsJsonAsync(path + "/rewind", new RewindSessionRequest(1, 0), token),
            _ => await server.Http.GetAsync(path + (route == "checkpoints" ? "/checkpoints" : "/details"), token),
        };
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        server.Sessions.Details(session.Id).ShouldBe(new(session.Id, 0, "Untitled conversation", false, 0, 0, 0, 0, false));
        server.Clients.Single().UserMessages.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("missing-details", 404)]
    [InlineData("missing-update", 404)]
    [InlineData("missing-checkpoints", 404)]
    [InlineData("missing-rewind", 404)]
    [InlineData("stale-rewind", 409)]
    [InlineData("unknown-revision", 404)]
    [InlineData("invalid-title", 400)]
    public async Task Should_PreserveSessionWithoutInference_When_MetadataRouteRejectsRequest(string partition, int status)
    {
        using var sandbox = new SettingsSandbox();
        using var store = new FileConversationStore(Path.Combine(sandbox.Root, "conversations"));
        using var sessions = new AgentSessionService(_ => new RecordingChatClient());
        var token = TestContext.Current.CancellationToken;
        await sessions.EnablePersistenceAsync(store, token);
        await using var server = await ServerFixture.StartAsync(sessions: sessions);
        var session = await server.CreateAsync();
        var path = $"/v1/sessions/{(partition.StartsWith("missing", StringComparison.Ordinal) ? Guid.NewGuid() : session.Id)}";
        using var response = partition switch
        {
            "missing-details" => await server.Http.GetAsync(path + "/details", token),
            "missing-checkpoints" => await server.Http.GetAsync(path + "/checkpoints", token),
            "missing-update" or "invalid-title" => await server.Http.PostAsJsonAsync(path + "/details", new UpdateSessionRequest(partition == "invalid-title" ? "\n" : "Title", true), token),
            _ => await server.Http.PostAsJsonAsync(path + "/rewind", new RewindSessionRequest(partition == "unknown-revision" ? 2 : 1, partition == "stale-rewind" ? 0 : 1), token),
        };
        ((int)response.StatusCode).ShouldBe(status);
        sessions.Details(session.Id).ShouldBe(new(session.Id, 1, "Untitled conversation", false, 0, 0, 0, 0, false));
        (await sessions.CheckpointsAsync(session.Id, token)).ShouldBe([1L]);
        sessions.Messages(session.Id).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(239)]
    [InlineData(240)]
    [InlineData(241)]
    public async Task Should_EnforceExactTitleBoundWithoutLosingMetadata_When_TitleLengthReachesLimit(int length)
    {
        using var sessions = new AgentSessionService(_ => new RecordingChatClient());
        var token = TestContext.Current.CancellationToken;
        var session = await sessions.CreateAsync("test/chat", token);
        var request = new UpdateSessionRequest(new string('t', length), true);
        if (length <= 240)
        {
            (await sessions.UpdateAsync(session.Id, request, token)).ShouldBe(new(session.Id, 0, request.Title, true, 0, 0, 0, 0, false));
            await Should.ThrowAsync<SessionConflictException>(() => sessions.SendAsync(session.Id, "blocked", cancellationToken: token));
        }
        else
        {
            await Should.ThrowAsync<ArgumentException>(() => sessions.UpdateAsync(session.Id, request, token));
            sessions.Details(session.Id).Title.ShouldBe("Untitled conversation");
            sessions.Details(session.Id).Archived.ShouldBeFalse();
        }
        sessions.Messages(session.Id).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("canceled")]
    public async Task Should_RecoverStableIdentityAndOnlyCommittedHistory_When_DurableTurnEnds(string outcome)
    {
        using var sandbox = new SettingsSandbox();
        var directory = Path.Combine(sandbox.Root, "conversations");
        var token = TestContext.Current.CancellationToken;
        AgentSessionDto original;
        using (var store = new FileConversationStore(directory))
        using (var sessions = new AgentSessionService(_ => new RecordingChatClient()))
        {
            await sessions.EnablePersistenceAsync(store, token);
            original = await sessions.CreateAsync("test/chat", token);
            await sessions.SendAsync(original.Id, "seed", cancellationToken: token);
            if (outcome == "success") await sessions.SendAsync(original.Id, "next", cancellationToken: token);
            else if (outcome == "failure") await Should.ThrowAsync<HttpRequestException>(() => sessions.SendAsync(original.Id, "fail", cancellationToken: token));
            else
            {
                using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
                await Should.ThrowAsync<OperationCanceledException>(() => sessions.SendAsync(original.Id, "cancel me", (item, _) =>
                {
                    if (item.Kind == "text.delta") canceled.Cancel();
                    return ValueTask.CompletedTask;
                }, canceled.Token));
            }
            sessions.Details(original.Id).Revision.ShouldBe(3);
        }
        using var restartedStore = new FileConversationStore(directory);
        var client = new RecordingChatClient();
        using var restarted = new AgentSessionService(_ => client);
        await restarted.EnablePersistenceAsync(restartedStore, token);
        restarted.Get(original.Id).ShouldBe(original with { MessageCount = outcome == "success" ? 4 : 2 });
        restarted.Details(original.Id).ShouldBe(new(original.Id, 3, "Untitled conversation", false, 0, 0, outcome == "success" ? 2 : 1, outcome == "success" ? 0 : 1, false));
        string[] history = outcome == "success" ? ["seed", "reply:seed", "next", "reply:next"] : ["seed", "reply:seed"];
        restarted.Messages(original.Id).Select(message => message.Text).ShouldBe(history);
        await restarted.SendAsync(original.Id, "resumed", cancellationToken: token);
        client.UserMessages.ShouldHaveSingleItem().ShouldBe(outcome == "success" ? ["seed", "next", "resumed"] : ["seed", "resumed"]);
        restarted.Messages(original.Id).Select(message => message.Sequence).ShouldBe(Enumerable.Range(1, history.Length + 2));
        restarted.Details(original.Id).Revision.ShouldBe(4);
    }

    [Fact]
    public async Task Should_RewindExactHistoryWithoutRefundingUsage_When_RevisionMatchesCurrent()
    {
        using var sandbox = new SettingsSandbox();
        using var store = new FileConversationStore(Path.Combine(sandbox.Root, "conversations"));
        var client = new RecordingChatClient { ReportedUsage = new() { InputTokenCount = 3, OutputTokenCount = 2 } };
        using var sessions = new AgentSessionService(_ => client);
        var token = TestContext.Current.CancellationToken;
        await sessions.EnablePersistenceAsync(store, token);
        var session = await sessions.CreateAsync("test/chat", token);
        await sessions.SendAsync(session.Id, "seed", cancellationToken: token);
        var originalHistory = sessions.Messages(session.Id).ToArray();
        await sessions.SendAsync(session.Id, "discarded", cancellationToken: token);
        await Should.ThrowAsync<HttpRequestException>(() => sessions.SendAsync(session.Id, "fail", cancellationToken: token));
        var current = await sessions.UpdateAsync(session.Id, new("Current title", false), token);
        current.ShouldBe(new(session.Id, 5, "Current title", false, 9, 6, 2, 1, true));
        await Should.ThrowAsync<SessionConflictException>(() => sessions.RewindAsync(session.Id, 2, 4, token));
        sessions.Messages(session.Id).Select(message => message.Text).ShouldBe(["seed", "reply:seed", "discarded", "reply:discarded"]);
        sessions.Details(session.Id).ShouldBe(current);

        (await sessions.RewindAsync(session.Id, 2, 5, token)).ShouldBe(session with { MessageCount = 2 });

        sessions.Messages(session.Id).ShouldBe(originalHistory);
        sessions.Details(session.Id).ShouldBe(current with { Revision = 6 });
        (await sessions.CheckpointsAsync(session.Id, token)).ShouldBe([6L, 5L, 4L, 3L, 2L, 1L]);
        (await store.ReadAsync(session.Id, 3, token)).Archive.Messages.Select(message => message.Text).ShouldBe(["seed", "reply:seed", "discarded", "reply:discarded"]);
        await sessions.SendAsync(session.Id, "continued", cancellationToken: token);
        client.UserMessages.Last().ShouldBe(["seed", "continued"]);
        sessions.Details(session.Id).ShouldBe(current with { Revision = 7, CompletedTurns = 3, InputTokens = 12, OutputTokens = 8 });
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    [InlineData(-1, false)]
    public async Task Should_AdmitOnlyReportedUsageBelowCap_When_NextTurnIsRequested(int reportedTokens, bool admitted)
    {
        var client = new RecordingChatClient { ReportedUsage = reportedTokens < 0 ? null : new() { InputTokenCount = reportedTokens - 2, OutputTokenCount = 2 } };
        using var sessions = new AgentSessionService(_ => client, new() { MaxSessionTokens = 10 });
        var token = TestContext.Current.CancellationToken;
        var session = await sessions.CreateAsync("test/chat", token);
        await sessions.SendAsync(session.Id, "first", cancellationToken: token);
        var initial = sessions.Details(session.Id);
        initial.InputTokens.ShouldBe(reportedTokens < 0 ? 0 : reportedTokens - 2);
        initial.OutputTokens.ShouldBe(reportedTokens < 0 ? 0 : 2);
        initial.TokensReported.ShouldBe(reportedTokens >= 0);
        if (admitted)
        {
            await sessions.SendAsync(session.Id, "second", cancellationToken: token);
            sessions.Messages(session.Id).Select(message => message.Text).ShouldBe(["first", "reply:first", "second", "reply:second"]);
            sessions.Details(session.Id).InputTokens.ShouldBe(14);
        }
        else
        {
            await Should.ThrowAsync<SessionConflictException>(() => sessions.SendAsync(session.Id, "blocked", cancellationToken: token));
            sessions.Details(session.Id).ShouldBe(initial);
            sessions.Messages(session.Id).Select(message => message.Text).ShouldBe(["first", "reply:first"]);
        }
        client.UserMessages.Count.ShouldBe(admitted ? 2 : 1);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("model-null")]
    [InlineData("model-empty")]
    [InlineData("model-long")]
    [InlineData("messages-null")]
    [InlineData("messages-odd")]
    [InlineData("message-null")]
    [InlineData("sequence")]
    [InlineData("user-role")]
    [InlineData("assistant-role")]
    [InlineData("text-null")]
    [InlineData("input-limit")]
    [InlineData("output-limit")]
    [InlineData("message-limit")]
    [InlineData("state-null")]
    [InlineData("state-array")]
    [InlineData("state-undefined")]
    [InlineData("state-framework")]
    public async Task Should_RejectInvalidArchiveWithoutLeakingClientsOrSlots_When_ImportValidationFails(string partition)
    {
        using var source = new AgentSessionService(_ => new RecordingChatClient());
        var token = TestContext.Current.CancellationToken;
        var original = await source.CreateAsync("test/chat", token);
        await source.SendAsync(original.Id, "one", cancellationToken: token);
        var valid = await source.ExportAsync(original.Id, token);
        var invalid = partition switch
        {
            "null" => null!,
            "version" => valid with { Version = 2 },
            "model-null" => valid with { Model = null! },
            "model-empty" => valid with { Model = " " },
            "model-long" => valid with { Model = new string('m', 513) },
            "messages-null" => valid with { Messages = null! },
            "messages-odd" => valid with { Messages = [valid.Messages[0]] },
            "message-null" => valid with { Messages = [null!, valid.Messages[1]] },
            "sequence" => valid with { Messages = [valid.Messages[0] with { Sequence = 2 }, valid.Messages[1]] },
            "user-role" => valid with { Messages = [valid.Messages[0] with { Role = "system" }, valid.Messages[1]] },
            "assistant-role" => valid with { Messages = [valid.Messages[0], valid.Messages[1] with { Role = "user" }] },
            "text-null" => valid with { Messages = [valid.Messages[0] with { Text = null! }, valid.Messages[1]] },
            "input-limit" => valid with { Messages = [valid.Messages[0] with { Text = new string('x', 9) }, valid.Messages[1]] },
            "output-limit" => valid with { Messages = [valid.Messages[0], valid.Messages[1] with { Text = new string('x', 17) }] },
            "message-limit" => valid with { Messages = [.. valid.Messages, valid.Messages[0] with { Sequence = 3 }, valid.Messages[1] with { Sequence = 4 }] },
            "state-null" => valid with { State = JsonSerializer.SerializeToElement<object?>(null) },
            "state-array" => valid with { State = JsonSerializer.SerializeToElement(Array.Empty<string>()) },
            "state-undefined" => valid with { State = default },
            _ => valid with { State = JsonSerializer.SerializeToElement(valid.State.EnumerateObject().ToDictionary(property => property.Name, _ => "invalid-framework-state")) },
        };
        var clients = new List<RecordingChatClient>();
        using var destination = new AgentSessionService(_ =>
        {
            var client = new RecordingChatClient();
            clients.Add(client);
            return client;
        }, new() { MaxSessions = 1, MaxMessages = 2, MaxInputCharacters = 8, MaxOutputCharacters = 16 });

        if (partition == "null") await Should.ThrowAsync<ArgumentNullException>(() => destination.ImportAsync(invalid, token));
        else await Should.ThrowAsync<ArgumentException>(() => destination.ImportAsync(invalid, token));

        destination.List().ShouldBeEmpty();
        clients.Count.ShouldBe(partition == "state-framework" ? 1 : 0);
        clients.ShouldAllBe(client => client.Disposed);
        var imported = await destination.ImportAsync(valid, token);
        destination.Messages(imported.Id).Select(message => message.Text).ShouldBe(["one", "reply:one"]);
        destination.List().Select(session => session.Id).ShouldBe([imported.Id]);
        clients.Last().Disposed.ShouldBeFalse();
        destination.Delete(imported.Id).ShouldBeTrue();
        clients.ShouldAllBe(client => client.Disposed);
    }

    [Theory]
    [InlineData(393215)]
    [InlineData(393216)]
    [InlineData(393217)]
    public async Task Should_EnforceExactTransferByteBudget_When_ArchiveIsAtBoundary(int bytes)
    {
        using var source = new AgentSessionService(_ => new RecordingChatClient());
        var original = await source.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        var archive = await source.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        var state = System.Text.Json.Nodes.JsonNode.Parse(archive.State.GetRawText())!.AsObject();
        state["padding"] = "";
        archive = archive with { State = JsonSerializer.SerializeToElement(state) };
        state["padding"] = new string('x', bytes - JsonSerializer.SerializeToUtf8Bytes(archive, JsonSerializerOptions.Web).Length);
        archive = archive with { State = JsonSerializer.SerializeToElement(state) };
        JsonSerializer.SerializeToUtf8Bytes(archive, JsonSerializerOptions.Web).Length.ShouldBe(bytes);
        var clients = new List<RecordingChatClient>();
        using var destination = new AgentSessionService(_ =>
        {
            var client = new RecordingChatClient();
            clients.Add(client);
            return client;
        }, new() { MaxSessions = 1 });

        if (bytes > 393216)
        {
            var error = await Should.ThrowAsync<ArgumentException>(() => destination.ImportAsync(archive, TestContext.Current.CancellationToken));
            error.Message.ShouldContain("384 KiB");
            destination.List().ShouldBeEmpty();
            clients.ShouldBeEmpty();
            (await destination.CreateAsync("test/chat", TestContext.Current.CancellationToken)).MessageCount.ShouldBe(0);
        }
        else
        {
            var imported = await destination.ImportAsync(archive, TestContext.Current.CancellationToken);
            imported.MessageCount.ShouldBe(0);
            destination.List().Select(session => session.Id).ShouldBe([imported.Id]);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Should_ReserveConservativeStateLengthBudget_When_ImportedHistoryIsEmpty(int offset)
    {
        using var source = new AgentSessionService(_ => new RecordingChatClient());
        var original = await source.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        var archive = await source.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        var client = new RecordingChatClient();
        using var destination = new AgentSessionService(_ => client, new() { MaxHistoryCharacters = archive.State.GetRawText().Length + offset });

        if (offset < 0)
        {
            await Should.ThrowAsync<ArgumentException>(() => destination.ImportAsync(archive, TestContext.Current.CancellationToken));
            destination.List().ShouldBeEmpty();
        }
        else
        {
            var imported = await destination.ImportAsync(archive, TestContext.Current.CancellationToken);
            await Should.ThrowAsync<SessionConflictException>(() => destination.SendAsync(imported.Id, "xx", cancellationToken: TestContext.Current.CancellationToken));
            destination.Messages(imported.Id).ShouldBeEmpty();
            client.UserMessages.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Should_EnforceTotalMessageCharacters_When_ArchiveHistoryReachesBudget(int offset)
    {
        using var source = new AgentSessionService(_ => new RecordingChatClient());
        var original = await source.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        var archive = await source.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        var length = archive.State.GetRawText().Length + 8;
        archive = archive with { Messages = [new(1, "user", new string('u', length), original.CreatedAt), new(2, "assistant", new string('a', length + offset), original.CreatedAt)] };
        var clients = new List<RecordingChatClient>();
        using var destination = new AgentSessionService(_ =>
        {
            var client = new RecordingChatClient();
            clients.Add(client);
            return client;
        }, new() { MaxSessions = 1, MaxInputCharacters = length + 1, MaxOutputCharacters = length + 1, MaxHistoryCharacters = length * 2 });

        if (offset > 0)
        {
            await Should.ThrowAsync<ArgumentException>(() => destination.ImportAsync(archive, TestContext.Current.CancellationToken));
            clients.ShouldBeEmpty();
            destination.List().ShouldBeEmpty();
        }
        else
        {
            var imported = await destination.ImportAsync(archive, TestContext.Current.CancellationToken);
            destination.Messages(imported.Id).Select(message => message.Text.Length).ShouldBe([length, length + offset]);
            await Should.ThrowAsync<SessionConflictException>(() => destination.SendAsync(imported.Id, "xx", cancellationToken: TestContext.Current.CancellationToken));
            clients.Single().UserMessages.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveOriginalAndRecoverCapacity_When_TransferExceedsSessionLimit(bool fork)
    {
        var clients = new List<RecordingChatClient>();
        using var sessions = new AgentSessionService(_ =>
        {
            var client = new RecordingChatClient();
            clients.Add(client);
            return client;
        }, new() { MaxSessions = 1 });
        var token = TestContext.Current.CancellationToken;
        var original = await sessions.CreateAsync("test/chat", token);
        await sessions.SendAsync(original.Id, "seed", cancellationToken: token);
        var archive = await sessions.ExportAsync(original.Id, token);

        await Should.ThrowAsync<SessionCapacityException>(() => fork ? sessions.ForkAsync(original.Id, token) : sessions.ImportAsync(archive, token));

        clients.Count.ShouldBe(1);
        clients.Single().Disposed.ShouldBeFalse();
        sessions.Messages(original.Id).Select(message => message.Text).ShouldBe(["seed", "reply:seed"]);
        sessions.Delete(original.Id).ShouldBeTrue();
        clients.Single().Disposed.ShouldBeTrue();
        var imported = await sessions.ImportAsync(archive, token);
        imported.Id.ShouldNotBe(original.Id);
        sessions.Messages(imported.Id).ShouldBe(archive.Messages);
    }

    [Fact]
    public async Task Should_RespectMessageCountAndTextBoundaries_When_ImportedHistoryExactlyFitsLimits()
    {
        using var sessions = new AgentSessionService(_ => new RecordingChatClient(),
            new() { MaxMessages = 2, MaxInputCharacters = 3, MaxOutputCharacters = 9 });
        var original = await sessions.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        await sessions.SendAsync(original.Id, "one", cancellationToken: TestContext.Current.CancellationToken);
        var archive = await sessions.ExportAsync(original.Id, TestContext.Current.CancellationToken);

        var imported = await sessions.ImportAsync(archive, TestContext.Current.CancellationToken);

        imported.MessageCount.ShouldBe(2);
        sessions.Messages(imported.Id).Select(message => message.Text).ShouldBe(["one", "reply:one"]);
        await Should.ThrowAsync<SessionConflictException>(() => sessions.SendAsync(imported.Id, "two", cancellationToken: TestContext.Current.CancellationToken));
        sessions.Messages(imported.Id).ShouldBe(archive.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_DisposeUncommittedClientAndReleaseReservation_When_ImportIsCanceled(bool afterClientCreation)
    {
        using var source = new AgentSessionService(_ => new RecordingChatClient());
        var original = await source.CreateAsync("test/chat", TestContext.Current.CancellationToken);
        var archive = await source.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var clients = new List<RecordingChatClient>();
        using var destination = new AgentSessionService(_ =>
        {
            var client = new RecordingChatClient();
            clients.Add(client);
            if (clients.Count == 1) cancellation.Cancel();
            return client;
        }, new() { MaxSessions = 1 });
        if (!afterClientCreation) cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => destination.ImportAsync(archive, cancellation.Token));

        destination.List().ShouldBeEmpty();
        clients.Count.ShouldBe(afterClientCreation ? 1 : 0);
        clients.ShouldAllBe(client => client.Disposed);
        var imported = await destination.ImportAsync(archive, TestContext.Current.CancellationToken);
        destination.List().Select(session => session.Id).ShouldBe([imported.Id]);
        clients.Last().Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_ExposeTransferLocationsAndCommittedHistory_When_ArchiveEndpointsSucceed()
    {
        await using var server = await ServerFixture.StartAsync();
        var original = await server.CreateAsync();
        using var sent = await server.SendAsync(original.Id, "seed");
        sent.EnsureSuccessStatusCode();
        using var exported = await server.Http.GetAsync($"/v1/sessions/{original.Id}/archive", TestContext.Current.CancellationToken);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var archive = (await exported.Content.ReadFromJsonAsync<AgentSessionArchive>(TestContext.Current.CancellationToken))!;
        archive.Messages.Select(message => message.Text).ShouldBe(["seed", "reply:seed"]);
        foreach (var action in new[] { "fork", "import" })
        {
            using var response = action == "fork"
                ? await server.Http.PostAsync($"/v1/sessions/{original.Id}/fork", null, TestContext.Current.CancellationToken)
                : await server.Http.PostAsJsonAsync("/v1/sessions/import", archive, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            var branch = (await response.Content.ReadFromJsonAsync<AgentSessionDto>(TestContext.Current.CancellationToken))!;
            branch.Id.ShouldNotBe(original.Id);
            branch.Model.ShouldBe("test/chat");
            branch.MessageCount.ShouldBe(2);
            response.Headers.Location!.ToString().ShouldBe($"/v1/sessions/{branch.Id}");
            server.Sessions.Messages(branch.Id).ShouldBe(archive.Messages);
        }
        server.Sessions.List().Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("export")]
    [InlineData("fork")]
    [InlineData("import")]
    public async Task Should_RequireAuthenticationWithoutMutation_When_TransferEndpointIsCalled(string action)
    {
        await using var server = await ServerFixture.StartAsync();
        var original = await server.CreateAsync();
        var archive = await server.Sessions.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        foreach (var token in new[] { null, "wrong-token" })
        {
            server.Http.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
            using var response = action switch
            {
                "export" => await server.Http.GetAsync($"/v1/sessions/{original.Id}/archive", TestContext.Current.CancellationToken),
                "fork" => await server.Http.PostAsync($"/v1/sessions/{original.Id}/fork", null, TestContext.Current.CancellationToken),
                _ => await server.Http.PostAsJsonAsync("/v1/sessions/import", archive, TestContext.Current.CancellationToken),
            };
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Bearer");
            server.Sessions.List().Select(session => session.Id).ShouldBe([original.Id]);
        }
    }

    [Theory]
    [InlineData("missing-export", 404)]
    [InlineData("missing-fork", 404)]
    [InlineData("invalid-version", 400)]
    [InlineData("null-body", 400)]
    [InlineData("invalid-json", 400)]
    [InlineData("missing-model", 404)]
    [InlineData("full-import", 429)]
    [InlineData("full-fork", 429)]
    public async Task Should_MapTransferErrorsWithoutAllocatingSessions_When_RequestCannotBeAdmitted(string partition, int status)
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxSessions = 1 });
        var original = await server.CreateAsync();
        var archive = await server.Sessions.ExportAsync(original.Id, TestContext.Current.CancellationToken);
        if (partition == "missing-model") server.Sessions.Delete(original.Id).ShouldBeTrue();
        using var response = partition switch
        {
            "missing-export" => await server.Http.GetAsync($"/v1/sessions/{Guid.NewGuid()}/archive", TestContext.Current.CancellationToken),
            "missing-fork" => await server.Http.PostAsync($"/v1/sessions/{Guid.NewGuid()}/fork", null, TestContext.Current.CancellationToken),
            "full-fork" => await server.Http.PostAsync($"/v1/sessions/{original.Id}/fork", null, TestContext.Current.CancellationToken),
            "null-body" => await server.Http.PostAsync("/v1/sessions/import", new StringContent("null", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken),
            "invalid-json" => await server.Http.PostAsync("/v1/sessions/import", new StringContent("{", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken),
            _ => await server.Http.PostAsJsonAsync("/v1/sessions/import", partition switch
            {
                "invalid-version" => archive with { Version = 2 },
                "missing-model" => archive with { Model = "missing/chat" },
                _ => archive,
            }, TestContext.Current.CancellationToken),
        };
        ((int)response.StatusCode).ShouldBe(status);
        server.Sessions.List().Count.ShouldBe(partition == "missing-model" ? 0 : 1);
        server.Clients.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Should_RejectRequest_When_BearerTokenIsMissingOrWrong(string? token)
    {
        await using var server = await ServerFixture.StartAsync();
        server.Http.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        using var response = await server.Http.GetAsync("/v1/health", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Single().Scheme.ShouldBe("Bearer");
    }

    [Theory]
    [InlineData("Origin", "https://untrusted.example")]
    [InlineData("Origin", "null")]
    [InlineData("Host", "untrusted.example")]
    public async Task Should_RejectBrowserOrRebindingRequest_When_OriginOrHostIsUntrusted(string header, string value)
    {
        await using var server = await ServerFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        request.Headers.Add(header, value);
        using var response = await server.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_AllowPreflightWithoutToken_When_OriginAndHeadersAreExplicitlyAllowed()
    {
        await using var server = await ServerFixture.StartAsync();
        server.Http.DefaultRequestHeaders.Authorization = null;
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/sessions");
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        using var response = await server.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe("http://localhost:3000");
        using var unauthorized = await server.Http.GetAsync("/v1/sessions", TestContext.Current.CancellationToken);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_ExecuteFrameworkAgentWithHistory_When_MessagesContinue()
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        using var first = await server.SendAsync(session.Id, "one");
        first.EnsureSuccessStatusCode();
        using var second = await server.SendAsync(session.Id, "two");
        second.EnsureSuccessStatusCode();
        var reply = await second.Content.ReadFromJsonAsync<AgentMessageDto>(TestContext.Current.CancellationToken);
        reply!.Text.ShouldBe("reply:two");
        server.Clients.Single().UserMessages.Last().ShouldBe(["one", "two"]);
        var history = await server.Http.GetFromJsonAsync<AgentMessageDto[]>($"/v1/sessions/{session.Id}/messages", TestContext.Current.CancellationToken);
        history.ShouldNotBeNull();
        history.Select(message => message.Text).ShouldBe(["one", "reply:one", "two", "reply:two"]);
        history.Select(message => message.Sequence).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Should_StreamOrderedJsonFrames_When_AgentProducesMultilineText()
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        using var response = await server.SendAsync(session.Id, "one\ntwo", stream: true);
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var events = text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<AgentTurnEvent>(line[6..], JsonSerializerOptions.Web)!).ToArray();
        events.Select(item => item.Kind).ShouldBe(["turn.started", "text.delta", "text.delta", "turn.completed"]);
        events.Select(item => item.Sequence).ShouldBe([1, 2, 3, 4]);
        events.Select(item => item.TurnId).Distinct().Count().ShouldBe(1);
        string.Concat(events.Where(item => item.Kind == "text.delta").Select(item => item.Text)).ShouldBe("reply:one\ntwo");
    }

    [Fact]
    public async Task Should_RollBackFailedTurnAndHideProviderDetails_When_ModelFails()
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        using var first = await server.SendAsync(session.Id, "one");
        first.EnsureSuccessStatusCode();
        using var failed = await server.SendAsync(session.Id, "fail");
        failed.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("SECRET_DO_NOT_LEAK");
        using var next = await server.SendAsync(session.Id, "two");
        next.EnsureSuccessStatusCode();
        server.Clients.Single().UserMessages.Last().ShouldBe(["one", "two"]);
        server.Sessions.Messages(session.Id).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Should_EmitTerminalFailureWithoutCommittingHistory_When_StreamingModelFails()
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        using var response = await server.SendAsync(session.Id, "fail", stream: true);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("event: turn.failed");
        text.ShouldNotContain("SECRET_DO_NOT_LEAK");
        text.ShouldNotContain("event: turn.completed");
        server.Sessions.Messages(session.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_RejectOverlappingTurnsAndCancelActiveTurn_When_SessionIsBusy()
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        var pending = server.SendAsync(session.Id, "wait");
        await server.Clients.Single().Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var conflict = await server.SendAsync(session.Id, "second");
        conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var blockedDelete = await server.Http.DeleteAsync($"/v1/sessions/{session.Id}", TestContext.Current.CancellationToken);
        blockedDelete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var cancel = await server.Http.PostAsync($"/v1/sessions/{session.Id}/cancel", null, TestContext.Current.CancellationToken);
        cancel.EnsureSuccessStatusCode();
        using var canceled = await pending;
        canceled.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        server.Sessions.Messages(session.Id).ShouldBeEmpty();
        server.Sessions.Get(session.Id).Running.ShouldBeFalse();
        using var after = await server.SendAsync(session.Id, "after");
        after.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Should_ReleaseCapacityAndDisposeClient_When_IdleSessionIsDeleted()
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxSessions = 1 });
        var session = await server.CreateAsync();
        using var full = await server.Http.PostAsJsonAsync("/v1/sessions", new CreateAgentSessionRequest(), TestContext.Current.CancellationToken);
        full.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        using var removed = await server.Http.DeleteAsync($"/v1/sessions/{session.Id}", TestContext.Current.CancellationToken);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        server.Clients.Single().Disposed.ShouldBeTrue();
        var replacement = await server.CreateAsync();
        replacement.Id.ShouldNotBe(session.Id);
    }

    [Fact]
    public async Task Should_KeepConversationsIsolated_When_TwoSessionsShareModel()
    {
        await using var server = await ServerFixture.StartAsync();
        var first = await server.CreateAsync();
        var second = await server.CreateAsync();
        using var firstReply = await server.SendAsync(first.Id, "private");
        using var secondReply = await server.SendAsync(second.Id, "different");
        firstReply.EnsureSuccessStatusCode();
        secondReply.EnsureSuccessStatusCode();
        server.Clients.Last().UserMessages.Single().ShouldBe(["different"]);
    }

    [Fact]
    public async Task Should_RejectInvalidAndOversizedMessages_When_InputViolatesLimits()
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxInputCharacters = 4 });
        var session = await server.CreateAsync();
        using var oversized = await server.SendAsync(session.Id, "too long");
        oversized.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var empty = await server.SendAsync(session.Id, " ");
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var malformed = await server.Http.PostAsync($"/v1/sessions/{session.Id}/messages", new StringContent("{", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        malformed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        server.Clients.Single().UserMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_AbortWithoutCommittingHistory_When_OutputLimitIsExceeded()
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxOutputCharacters = 4 });
        var session = await server.CreateAsync();
        using var response = await server.SendAsync(session.Id, "one");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        server.Sessions.Messages(session.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_CountUnpublishedReasoning_When_EnforcingOutputLimit()
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxOutputCharacters = 10 });
        var session = await server.CreateAsync();
        using var response = await server.SendAsync(session.Id, "reason");
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        server.Sessions.Messages(session.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_EnforceTurnDeadline_When_ModelDoesNotFinish()
    {
        await using var server = await ServerFixture.StartAsync(new() { TurnTimeout = TimeSpan.FromMilliseconds(100) });
        var session = await server.CreateAsync();
        using var response = await server.SendAsync(session.Id, "wait");
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        server.Sessions.Get(session.Id).Running.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_ExposeCatalogAndOpenApiWithoutSecrets_When_Authenticated()
    {
        await using var server = await ServerFixture.StartAsync();
        var providers = await server.Http.GetFromJsonAsync<CatalogProviderDto[]>("/v1/catalog/providers", TestContext.Current.CancellationToken);
        providers!.Single().Protocol.ShouldBe("chat");
        using var document = await server.Http.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        document.EnsureSuccessStatusCode();
        var text = await document.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldContain("/v1/sessions");
        text.ShouldContain("/v1/catalog/providers");
        text.ShouldNotContain(ServerFixture.Token);
        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer").GetProperty("scheme").GetString().ShouldBe("bearer");
        json.RootElement.GetProperty("paths").GetProperty("/v1/health").GetProperty("get").GetProperty("security")[0].GetProperty("Bearer").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task Should_PreserveCorsAndNoStoreHeaders_When_AllowedBrowserRequestFails()
    {
        await using var server = await ServerFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/sessions/{Guid.NewGuid()}");
        request.Headers.Add("Origin", "http://localhost:3000");
        using var response = await server.Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe("http://localhost:3000");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Should_RejectFurtherTurnsWithoutDroppingHistory_When_MessageCountLimitIsReached()
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxMessages = 2 });
        var session = await server.CreateAsync();
        using var first = await server.SendAsync(session.Id, "one");
        first.EnsureSuccessStatusCode();
        using var second = await server.SendAsync(session.Id, "two");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        server.Sessions.Messages(session.Id).Count.ShouldBe(2);
        server.Clients.Single().UserMessages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Should_EnforceGlobalTurnCapacity_When_AnotherSessionIsRunning()
    {
        await using var server = await ServerFixture.StartAsync(new() { MaxConcurrentTurns = 1 });
        var first = await server.CreateAsync();
        var second = await server.CreateAsync();
        var pending = server.SendAsync(first.Id, "wait");
        await server.Clients.First().Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var full = await server.SendAsync(second.Id, "two");
        full.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        await server.Sessions.CancelAsync(first.Id);
        using var canceled = await pending;
        canceled.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        using var after = await server.SendAsync(second.Id, "two");
        after.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Should_RejectCatalogRefresh_When_ConfigurationDisablesNetworkRefresh()
    {
        await using var server = await ServerFixture.StartAsync(allowCatalogRefresh: false);
        using var response = await server.Http.PostAsync("/v1/catalog/refresh", null, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Should_CancelModelAndDisposeClient_When_HostStopsDuringTurn()
    {
        await using var server = await ServerFixture.StartAsync();
        var session = await server.CreateAsync();
        var pending = server.SendAsync(session.Id, "wait");
        await server.Clients.Single().Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var response = await pending;
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        server.Clients.Single().Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1:0", "", null)]
    [InlineData("0.0.0.0:8080", ServerFixture.Token, null)]
    [InlineData("127.0.0.1:0", ServerFixture.Token, "*")]
    [InlineData("127.0.0.1:0", ServerFixture.Token, "https://example.test/path")]
    [InlineData("127.0.0.1:0", ServerFixture.Token, "https://user:password@example.test")]
    public void Should_RejectUnsafeConfigurationBeforeListening_When_ServerIsBuilt(string address, string token, string? origin)
    {
        using var sessions = new AgentSessionService(_ => new RecordingChatClient());
        var options = new HttpServerOptions { Address = address, Token = token, AllowedOrigins = origin is null ? [] : [origin] };
        Should.Throw<ArgumentException>(() => CodeAssistHttpServer.Build(options, sessions, new(Substitute.For<IModelCatalog>())));
    }
}