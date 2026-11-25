using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public enum WorkState { Queued, Active, Done, Cancelled }

public sealed record WorkItem(Guid Id, string Title, string Detail, string? Owner, WorkState State,
    IReadOnlyList<Guid> Requires, int Revision);

public sealed record WorkItemChanged(Guid SessionId, long Seq, DateTimeOffset At, WorkItem Item, bool Removed = false)
    : JournalEntry(SessionId, Seq, At);