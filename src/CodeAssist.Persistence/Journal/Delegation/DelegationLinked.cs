using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record DelegationLinked(Guid SessionId, long Seq, DateTimeOffset At, Guid Parent, Guid Root, string Role, string Model, int Depth, IReadOnlyList<string>? AllowedTools = null) : JournalEntry(SessionId, Seq, At);