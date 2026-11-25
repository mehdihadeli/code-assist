using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record PlanningChanged(Guid SessionId, long Seq, DateTimeOffset At, bool Active, string? ApprovedProposal)
    : JournalEntry(SessionId, Seq, At);