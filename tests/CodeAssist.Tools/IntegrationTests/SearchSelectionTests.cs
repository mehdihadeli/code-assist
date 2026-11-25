using CodeAssist.Tools;
using CodeAssist.Sessions;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class SearchSelectionTests
{
    [Fact]
    public async Task Should_AllowIndependentQuestions_When_TwoSessionsSelectProviders()
    {
        var backend = Substitute.For<IWebSearch>();
        backend.ProviderId.Returns("fixture");
        var broker = new QuestionBroker();
        var service = new SessionWebSearch([backend], broker);
        var first = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var second = new ToolContext { Workspace = first.Workspace, SessionId = Guid.NewGuid() };
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        broker.Requested += _ => { if (Interlocked.Increment(ref count) == 2) pending.TrySetResult(); };
        var token = TestContext.Current.CancellationToken;
        var firstSearch = service.SearchAsync("first", first, token);
        var secondSearch = service.SearchAsync("second", second, token);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        broker.Answer(first.SessionId, broker.PendingFor(first.SessionId).Single().Id, [new(["fixture"], null)]).ShouldBeTrue();
        broker.Answer(second.SessionId, broker.PendingFor(second.SessionId).Single().Id, [new(["fixture"], null)]).ShouldBeTrue();
        await Task.WhenAll(firstSearch, secondSearch);
    }

    [Fact]
    public async Task Should_IsolateSelectionAndDeclareEveryDestination_When_SearchingAcrossSessions()
    {
        var first = Substitute.For<IWebSearch>();
        first.ProviderId.Returns("first");
        var second = Substitute.For<IWebSearch>();
        second.ProviderId.Returns("second");
        var interaction = Substitute.For<IUserInteraction>();
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var other = new ToolContext { Workspace = context.Workspace, SessionId = Guid.NewGuid() };
        interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), context, Arg.Any<CancellationToken>()).Returns([new QuestionAnswer(["second"], null)]);
        interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), other, Arg.Any<CancellationToken>()).Returns([new QuestionAnswer(["Disable for session"], null)]);
        var service = new SessionWebSearch([first, second], interaction);
        var token = TestContext.Current.CancellationToken;
        await service.SearchAsync("query", context, token);
        await service.SearchAsync("again", context, token);
        await interaction.Received(1).AskAsync(Arg.Any<IReadOnlyList<Question>>(), context, token);
        await second.Received(1).SearchAsync("query", context, token);
        await Should.ThrowAsync<InvalidOperationException>(() => service.SearchAsync("private", other, token));
        await first.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<ToolContext>(), token);
        var plan = await new WebSearchTool(service).PlanAsync(ToolArguments.Parse("""{"query":"test"}"""), context, token);
        plan.Effects.Select(effect => effect.Target).ShouldBe(["first", "second"]);
    }
}