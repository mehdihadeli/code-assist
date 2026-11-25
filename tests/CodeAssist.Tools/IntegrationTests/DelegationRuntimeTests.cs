using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Configuration;
using CodeAssist.Server.Composition;
using CodeAssist.Models.Chat;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class DelegationRuntimeTests
{
    [Fact]
    public async Task Should_NeverRegainAncestorDeniedTools_When_DelegatingAndResumingNestedChildren()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-delegate-ancestry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(new(Path.Combine(root, "home"), root, Path.Combine(root, "managed")), new DictionaryEnvironmentVariables(new Dictionary<string, string>()), new FlagOverrides());
            settings.Current.Settings.Agent.MaxDelegationDepth = 3;
            settings.Current.Settings.Roles["builder"].Tools.Allow = ["read"];
            settings.Current.Settings.Roles["reviewer"] = new() { Delegatable = true };
            var journal = new JsonlJournalStore(Path.Combine(root, "journal"));
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Role = "builder" };
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, root, "parent/model"), 0, TestContext.Current.CancellationToken);
            var read = Substitute.For<ITool>();
            read.Spec.Returns(new ToolSpec("fs.read", "Read", "{\"type\":\"object\"}", ToolRisk.Read, ["read"]));
            var write = Substitute.For<ITool>();
            write.Spec.Returns(new ToolSpec("fs.write", "Write", "{\"type\":\"object\"}", ToolRisk.Write, ["write"]));
            var offered = new List<string[]>();
            var selectedModels = new List<string>();
            var model = Substitute.For<IAgentModel>();
            model.RespondAsync(Arg.Any<AgentModelRequest>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                offered.Add(call.ArgAt<AgentModelRequest>(0).Tools.Select(spec => spec.Name).ToArray());
                selectedModels.Add(call.ArgAt<AgentModelRequest>(0).Model);
                return new AgentModelReply("Done", [], 1, 1);
            });
            var runtime = new JournalDelegation(journal, settings, model, () => new ToolCatalog([read, write]), () => Substitute.For<IToolDispatcher>());
            var request = new DelegationRequest("reviewer", "Review", "Review safely", null, null);
            var child = await runtime.RunAsync(request, context, TestContext.Current.CancellationToken);
            settings.Current.Settings.Roles["builder"].Tools.Allow = [];
            await runtime.RunAsync(request with { ResumeSessionId = child.SessionId, Model = "parent/other@review" }, context, TestContext.Current.CancellationToken);
            await runtime.RunAsync(request, new ToolContext { Workspace = root, SessionId = child.SessionId, Role = "reviewer" }, TestContext.Current.CancellationToken);
            offered.Count.ShouldBe(3);
            offered.ShouldAllBe(names => names.SequenceEqual(new[] { "fs.read" }));
            selectedModels.ShouldBe(["parent/model", "parent/other@review", "parent/other@review"]);
            (await journal.ReadAsync(child.SessionId, TestContext.Current.CancellationToken)).OfType<DelegationModelSelected>().Single().Model.ShouldBe("parent/other@review");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Should_UseRealProviderHttpAndDurableHistory_When_RunningChildModels()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/chat/completions").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"id":"fixture","object":"chat.completion","model":"fixture","choices":[{"index":0,"message":{"role":"assistant","content":"HTTP review complete"},"finish_reason":"stop"}],"usage":{"prompt_tokens":8,"completion_tokens":4,"total_tokens":12}}"""));
        var root = Path.Combine(Path.GetTempPath(), "ca-delegate-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(new(Path.Combine(root, "home"), root, Path.Combine(root, "managed")), new DictionaryEnvironmentVariables(new Dictionary<string, string>()), new FlagOverrides());
            settings.Current.Settings.Roles["reviewer"] = new() { Delegatable = true };
            var journal = new JsonlJournalStore(Path.Combine(root, "journal"));
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Role = "builder" };
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            var adapter = new ChatClientAgentModel(_ => ProviderComposition.CreateChatClient("default", "fixture", new ProviderSettings { Endpoint = server.Url!, Api = ProviderApi.Chat }, ProviderRegistry.CreateDefault(), new Dictionary<string, string>()));
            var runtime = new JournalDelegation(journal, settings, adapter, () => new ToolCatalog([]), () => Substitute.For<IToolDispatcher>());
            var result = await runtime.RunAsync(new("reviewer", "Review change", "Review this task", null, null), context, TestContext.Current.CancellationToken);
            result.IsError.ShouldBeFalse(result.Text);
            result.Text.ShouldBe("HTTP review complete");
            server.LogEntries.ShouldHaveSingleItem();
            (await journal.ReadAsync(context.SessionId, TestContext.Current.CancellationToken)).OfType<DelegationUsage>().Single().OutputTokens.ShouldBe(4);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_StopBeforeUnauthorizedEffects_When_RoleOrBudgetDisallowsCalls(bool exceedsBudget)
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-delegate-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(new(Path.Combine(root, "home"), root, Path.Combine(root, "managed")), new DictionaryEnvironmentVariables(new Dictionary<string, string>()), new FlagOverrides());
            settings.Current.Settings.Roles["reviewer"] = new() { Delegatable = true, Tools = new() { Allow = ["read"] } };
            var journal = new JsonlJournalStore(Path.Combine(root, "journal"));
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Role = "builder" };
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            var model = Substitute.For<IAgentModel>();
            model.RespondAsync(Arg.Any<AgentModelRequest>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(
                new AgentModelReply("", [new("call-1", "forbidden", "{}")], exceedsBudget ? settings.Current.Settings.Agent.MaxTokens + 1 : 1, 1), new AgentModelReply("Finished", [], 1, 1));
            var dispatcher = Substitute.For<IToolDispatcher>();
            var runtime = new JournalDelegation(journal, settings, model, () => new ToolCatalog([]), () => dispatcher);
            var request = new DelegationRequest("reviewer", "Review change", "Review this task", null, null);
            if (exceedsBudget) await Should.ThrowAsync<InvalidOperationException>(() => runtime.RunAsync(request, context, TestContext.Current.CancellationToken));
            else (await runtime.RunAsync(request, context, TestContext.Current.CancellationToken)).Text.ShouldBe("Finished");
            await dispatcher.DidNotReceive().InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Should_RunRecoverAndRestrictChildIdentity_When_DelegatingTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-delegate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new SettingsService(new(Path.Combine(root, "home"), root, Path.Combine(root, "managed")), new DictionaryEnvironmentVariables(new Dictionary<string, string>()), new FlagOverrides());
            settings.Current.Settings.Roles["reviewer"] = new() { Delegatable = true, Tools = new() { Allow = ["read"] } };
            var journal = new JsonlJournalStore(Path.Combine(root, "journal"));
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Role = "builder" };
            await journal.AppendAsync(new SessionStarted(context.SessionId, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            var model = Substitute.For<IAgentModel>();
            model.RespondAsync(Arg.Any<AgentModelRequest>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new AgentModelReply("Reviewed", [], 20, 10));
            var dispatcher = Substitute.For<IToolDispatcher>();
            var runtime = new JournalDelegation(journal, settings, model, () => new ToolCatalog([]), () => dispatcher);
            var request = new DelegationRequest("reviewer", "Review changes", "Inspect this change", null, null);
            var result = await runtime.RunAsync(request, context, TestContext.Current.CancellationToken);
            result.IsError.ShouldBeFalse();
            result.Text.ShouldBe("Reviewed");
            var resumed = await runtime.RunAsync(request with { ResumeSessionId = result.SessionId }, context, TestContext.Current.CancellationToken);
            resumed.SessionId.ShouldBe(result.SessionId);
            var other = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Role = "builder" };
            await journal.AppendAsync(new SessionStarted(other.SessionId, 0, default, root, null), 0, TestContext.Current.CancellationToken);
            await Should.ThrowAsync<UnauthorizedAccessException>(() => runtime.RunAsync(request with { ResumeSessionId = result.SessionId }, other, TestContext.Current.CancellationToken));
            await Should.ThrowAsync<UnauthorizedAccessException>(() => runtime.RunAsync(request, new ToolContext { Workspace = root, SessionId = result.SessionId, Role = "reviewer" }, TestContext.Current.CancellationToken));
            (await journal.ReadAsync(context.SessionId, TestContext.Current.CancellationToken)).OfType<DelegationUsage>().Count().ShouldBe(2);
        }
        finally { Directory.Delete(root, true); }
    }
}