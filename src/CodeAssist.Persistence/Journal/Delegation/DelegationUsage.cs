using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record DelegationUsage(Guid SessionId, long Seq, DateTimeOffset At, Guid Child, long InputTokens, long OutputTokens, decimal? CostUsd) : JournalEntry(SessionId, Seq, At);