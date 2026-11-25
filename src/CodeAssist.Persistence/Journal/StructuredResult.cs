using CodeAssist.Contracts;
using System.Text.Json;

namespace CodeAssist.Persistence;

public sealed record StructuredResultSubmitted(Guid SessionId, long Seq, DateTimeOffset At, string Contract, string CallId, JsonElement Value)
    : JournalEntry(SessionId, Seq, At);