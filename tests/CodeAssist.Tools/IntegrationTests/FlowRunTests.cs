using CodeAssist.Server.Composition;
using CodeAssist.Tools;
using CodeAssist.Tools.Execution;
using CodeAssist.Security;
using CodeAssist.Contracts;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class FlowRunTests
{
    private static readonly ToolContext Context = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid(), CallId = "flow" };

    [Fact]
    public async Task FlowAuthorizesResolvedEffectsAndSkipsDeniedDependencies()
    {
        var writes = 0;
        var source = new FakeTool("source", ToolRisk.Read, (_, _) => Task.FromResult(ToolResult.Ok("{\"path\":\"secret\"}")));
        var target = new FakeTool("target", ToolRisk.Write, (_, _) => { writes++; return Task.FromResult(ToolResult.Ok("written")); });
        var authorizer = Substitute.For<IToolAuthorization>();
        authorizer.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ToolAuthorization(call.ArgAt<ToolSpec>(0).Name != "target", "blocked"));
        var dispatcher = new ToolDispatcher(_ => new ToolCatalog([source, target]), authorizer);
        var tool = new FlowRunTool(dispatcher);
        var result = await tool.ExecuteAsync(ToolArguments.Parse("""
            {"steps":[{"id":"read","tool":"source"},{"id":"write","tool":"target","bindings":[{"argument":"path","step":"read","select":"json:/path"}]},
            {"id":"skip","tool":"target","after":["write"]}]}
            """), Context, TestContext.Current.CancellationToken);
        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("dependency failed");
        writes.ShouldBe(0);
        await authorizer.Received(1).AuthorizeAsync(target.Spec, Arg.Is<ToolPlan>(plan => plan.Effects.Single().Target == "secret"), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlowRunsIndependentReadsInParallelAndReducesInInputOrder()
    {
        var entered = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new FakeTool("read", ToolRisk.Read, async (arguments, token) =>
        {
            if (Interlocked.Increment(ref entered) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return ToolResult.Ok(arguments.RequireString("path"));
        });
        var authorization = Substitute.For<IToolAuthorization>();
        authorization.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new ToolAuthorization(true));
        var tool = new FlowRunTool(new ToolDispatcher(_ => new ToolCatalog([read]), authorization));
        var result = await tool.ExecuteAsync(ToolArguments.Parse("""
            {"steps":[{"id":"first","tool":"read","arguments":{"path":"one"}},{"id":"second","tool":"read","arguments":{"path":"two"}}],"reduction":"join"}
            """), Context, TestContext.Current.CancellationToken);
        result.Text.ShouldBe("\"one\\ntwo\"");
        entered.ShouldBe(2);
    }

    [Fact]
    public async Task FlowDispatchedThroughSamePipelineDoesNotDeadlockItsWrites()
    {
        var active = 0;
        var writes = 0;
        var write = new FakeTool("write", ToolRisk.Write, async (_, token) =>
        {
            Interlocked.Increment(ref active).ShouldBe(1);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref writes);
            Interlocked.Decrement(ref active);
            return ToolResult.Ok("saved");
        });
        var authorization = Substitute.For<IToolAuthorization>();
        authorization.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new ToolAuthorization(true));
        ToolCatalog? catalog = null;
        var dispatcher = new ToolDispatcher(_ => catalog!, authorization);
        var flow = new FlowRunTool(dispatcher);
        catalog = new ToolCatalog([flow, write]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await dispatcher.InvokeAsync("flow.run", """{"steps":[{"id":"one","tool":"write"},{"id":"two","tool":"write"}],"reduction":"count"}""", Context, deadline.Token);
        result.Text.ShouldBe("2");
        writes.ShouldBe(2);
        await authorization.Received(3).AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlowRejectsCyclesMissingToolsAndRecursionAndPropagatesCancellation()
    {
        var dispatcher = Substitute.For<IToolDispatcher>();
        var tool = new FlowRunTool(dispatcher);
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"steps":[{"id":"a","tool":"x","after":["a"]}]}"""), Context, TestContext.Current.CancellationToken));
        (await tool.ExecuteAsync(ToolArguments.Parse("""{"steps":[{"id":"a","tool":"missing"}]}"""), Context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        dispatcher.Describe("flow.run", Context).Returns(tool.Spec);
        (await tool.ExecuteAsync(ToolArguments.Parse("""{"steps":[{"id":"a","tool":"flow.run"}]}"""), Context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.PlanAsync(ToolArguments.Parse("""{"steps":[{"id":"a","tool":"x"}]}"""), Context, cancelled.Token).AsTask());
    }

    [Fact]
    public async Task Should_AuthorizeAndBoundCustomCalls_When_SourceIsRegistered()
    {
        var executions = 0;
        var custom = new FakeTool("acme.lookup", ToolRisk.Read, (_, _) =>
        {
            executions++;
            return Task.FromResult(ToolResult.Ok(new string('x', 500)));
        });
        var settings = new CodeAssistSettings();
        var catalog = ToolComposition.CreateCatalog(settings, new ToolPorts(Sources: [new ToolSource([custom])]));
        var authorization = Substitute.For<IToolAuthorization>();
        authorization.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolAuthorization(false, "restricted"));
        var dispatcher = new ToolDispatcher(_ => catalog, authorization);
        var denied = await dispatcher.InvokeAsync("acme.lookup", "{}", Context, TestContext.Current.CancellationToken);
        denied.IsError.ShouldBeTrue();
        executions.ShouldBe(0);

        authorization.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new ToolAuthorization(true));
        var bounded = new ToolContext { Workspace = Context.Workspace, Limits = new ToolLimits { OutputMaxBytes = 32 } };
        var allowed = await dispatcher.InvokeAsync("acme.lookup", "{}", bounded, TestContext.Current.CancellationToken);
        allowed.Text.ShouldContain("[output truncated]");
        allowed.Text.Length.ShouldBeLessThan(80);
        executions.ShouldBe(1);

        settings.Tools.Disabled = ["acme.lookup"];
        ToolComposition.CreateCatalog(settings, new ToolPorts(Sources: [new ToolSource([custom])])).Find("acme.lookup").ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => ToolComposition.CreateCatalog(new ToolSettings(),
            new ToolPorts(Sources: [new ToolSource([custom, custom])])));
    }

    [Fact]
    public async Task Should_RejectInvalidCustomInputBeforePlanning_When_SchemaConstraintsFail()
    {
        var tool = Substitute.For<ITool>();
        tool.Spec.Returns(new ToolSpec("acme.strict", "Validate a count.", """{"type":"object","properties":{"count":{"type":"integer","minimum":1}},"required":["count"],"additionalProperties":false}""", ToolRisk.Read, ["read"]));
        var authorization = Substitute.For<IToolAuthorization>();
        var dispatcher = new ToolDispatcher(_ => new ToolCatalog([tool]), authorization);
        var result = await dispatcher.InvokeAsync("acme.strict", """{"count":0}""", Context, TestContext.Current.CancellationToken);
        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("input schema");
        await tool.DidNotReceive().PlanAsync(Arg.Any<ToolArguments>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        await authorization.DidNotReceive().AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        await tool.DidNotReceive().ExecuteAsync(Arg.Any<ToolArguments>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Should_RegisterCustomToolOnlyOnce_When_DiRegistrationIsRepeated()
    {
        var services = new ServiceCollection();
        services.AddCodeAssistTool<CustomLookupTool>().AddCodeAssistTool<CustomLookupTool>();
        using var provider = services.BuildServiceProvider();
        provider.GetServices<ITool>().ShouldHaveSingleItem().Spec.Name.ShouldBe("acme.lookup");
    }

    public sealed class CustomLookupTool : ITool
    {
        public ToolSpec Spec { get; } = new("acme.lookup", "Read a custom value.", "{}", ToolRisk.Read, ["read"]);
        public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) =>
            new(new ToolPlan([new(ToolEffectKind.Read, "acme:lookup")]));
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok("custom value"));
    }

    private sealed class FakeTool(string name, ToolRisk risk, Func<ToolArguments, CancellationToken, Task<ToolResult>> run) : ITool
    {
        public ToolSpec Spec { get; } = new(name, "test", "{}", risk, []);
        public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) =>
            new(new ToolPlan([new(risk == ToolRisk.Read ? ToolEffectKind.Read : ToolEffectKind.Write, arguments.String("path") ?? "test")]));
        public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken) => run(arguments, cancellationToken);
    }
}