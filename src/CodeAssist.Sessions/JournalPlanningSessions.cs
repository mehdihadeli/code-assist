using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalPlanningSessions(IJournalStore journal, IUserInteraction interaction) : IPlanningSessions
{
    public async Task<PlanningStatus> ReadAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var entries = await ReadEntriesAsync(context, cancellationToken);
        var last = entries.OfType<PlanningChanged>().LastOrDefault();
        return new(last?.Active ?? false, last?.Seq ?? 0);
    }

    public async Task<PlanningStatus> EnterAsync(ToolContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entries = await ReadEntriesAsync(context, cancellationToken);
            if (entries.OfType<PlanningChanged>().LastOrDefault() is { Active: true } current) return new(true, current.Seq);
            try
            {
                var entry = await journal.AppendAsync(new PlanningChanged(context.SessionId, 0, default, true, null), entries[^1].Seq, cancellationToken);
                return new(true, entry.Seq);
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    public async Task<PlanningStatus> RequestApplyAsync(string proposal, ToolContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proposal) || proposal.Length > 16000) throw new ArgumentException("Plan proposal must contain 1-16000 characters.");
        var before = await ReadAsync(context, cancellationToken);
        if (!before.Active) throw new InvalidOperationException("Enter planning before requesting plan approval.");
        var answers = await interaction.AskAsync([new Question("Plan Review", proposal,
            [new QuestionOption("Apply"), new QuestionOption("Keep planning")], false)], context, cancellationToken);
        if (answers is not { Count: 1 } || answers[0].Choices.Count != 1 || answers[0].Choices[0] != "Apply") return await ReadAsync(context, cancellationToken);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entries = await ReadEntriesAsync(context, cancellationToken);
            var current = entries.OfType<PlanningChanged>().LastOrDefault();
            if (current is not { Active: true } || current.Seq != before.Revision)
                throw new InvalidOperationException("Planning state changed during review; request approval again.");
            try
            {
                var entry = await journal.AppendAsync(new PlanningChanged(context.SessionId, 0, default, false, proposal), entries[^1].Seq, cancellationToken);
                return new(false, entry.Seq);
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    private async Task<IReadOnlyList<JournalEntry>> ReadEntriesAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty) throw new UnauthorizedAccessException("Planning requires a real session.");
        var entries = await journal.ReadAsync(context.SessionId, cancellationToken);
        if (entries.FirstOrDefault() is not SessionStarted) throw new IOException("Session does not exist.");
        return entries;
    }
}