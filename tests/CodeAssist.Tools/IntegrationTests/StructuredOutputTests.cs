using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class StructuredOutputTests : IDisposable
{
    private const string Schema = """{"type":"object","properties":{"count":{"type":"integer","minimum":1}},"required":["count"],"additionalProperties":false}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-output-" + Guid.NewGuid().ToString("N"));
    private readonly ToolContext _context = new() { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid(), CallId = "submit-1" };
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<JournalStructuredOutput> StartAsync()
    {
        var journal = new JsonlJournalStore(_root);
        await journal.AppendAsync(new SessionStarted(_context.SessionId, 0, default, _context.Workspace, null), 0, Token);
        return new JournalStructuredOutput(journal, "summary-v1", Schema);
    }

    [Fact]
    public async Task Should_CommitOnceAndReplayIdenticalRetry_When_OutputIsSubmitted()
    {
        var output = await StartAsync();
        var value = ToolArguments.Parse("""{"count":2}""").Json;
        var receipt = await output.SubmitAsync(value, _context, Token);
        receipt.Replayed.ShouldBeFalse();
        var reopened = new JournalStructuredOutput(new JsonlJournalStore(_root), "summary-v1", Schema);
        var replay = await reopened.SubmitAsync(value, _context, Token);
        replay.Replayed.ShouldBeTrue();
        replay.Sequence.ShouldBe(receipt.Sequence);
        await Should.ThrowAsync<InvalidOperationException>(() => reopened.SubmitAsync(ToolArguments.Parse("""{"count":3}""").Json, _context, Token));
        (await new JsonlJournalStore(_root).ReadAsync(_context.SessionId, Token)).OfType<StructuredResultSubmitted>().ShouldHaveSingleItem().Value.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Should_RejectInvalidOrOversizedResultsWithoutAppending_When_ContractIsEnforced()
    {
        var output = await StartAsync();
        var tool = new StructuredOutputTool(output);
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"count":0}"""), _context, Token));
        await Should.ThrowAsync<ToolArgumentException>(() => tool.PlanAsync(ToolArguments.Parse("""{"count":1,"extra":true}"""), _context, Token).AsTask());
        var tiny = new ToolContext { Workspace = _context.Workspace, SessionId = _context.SessionId, CallId = "tiny", Limits = new ToolLimits { OutputMaxBytes = 2 } };
        await Should.ThrowAsync<ArgumentException>(() => output.SubmitAsync(ToolArguments.Parse("""{"count":1}""").Json, tiny, Token));
        (await new JsonlJournalStore(_root).ReadAsync(_context.SessionId, Token)).Count.ShouldBe(1);
        (await tool.PlanAsync(ToolArguments.Parse("""{"count":1}"""), _context, Token)).Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Write);
        (await tool.ExecuteAsync(ToolArguments.Parse("""{"count":1}"""), _context, Token)).IsError.ShouldBeFalse();
    }
}