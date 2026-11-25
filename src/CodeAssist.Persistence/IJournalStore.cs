using CodeAssist.Contracts;

namespace CodeAssist.Persistence;

public sealed class JournalConflictException() : Exception("Session journal changed; retry against current sequence.");

public interface IJournalStore
{
    Task<IReadOnlyList<JournalEntry>> ReadAsync(Guid sessionId, CancellationToken cancellationToken);
    Task<JournalEntry> AppendAsync(JournalEntry entry, long expectedSequence, CancellationToken cancellationToken);
}