using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record DelegationModelSelected(Guid SessionId, long Seq, DateTimeOffset At, string Model) : JournalEntry(SessionId, Seq, At);