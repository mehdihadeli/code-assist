using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record TeamRecord(Guid Id, string Name, Guid Leader, IReadOnlyList<Guid> Members,
    IReadOnlyList<Guid> Invitations, bool Closed, int Revision);

public sealed record TeamChanged(Guid SessionId, long Seq, DateTimeOffset At, TeamRecord Team) : JournalEntry(SessionId, Seq, At);

public sealed record TeamLetter(Guid Id, Guid Sender, IReadOnlyList<Guid> Recipients, string Topic, string Text, Guid? ReplyTo, string Kind = "text", bool Broadcast = false);

public sealed record TeamLetterSent(Guid SessionId, long Seq, DateTimeOffset At, TeamLetter Letter, string? CallId = null) : JournalEntry(SessionId, Seq, At);

public sealed record TeamLetterAcknowledged(Guid SessionId, long Seq, DateTimeOffset At, Guid LetterId, Guid Reader) : JournalEntry(SessionId, Seq, At);