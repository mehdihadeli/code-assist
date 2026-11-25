namespace CodeAssist.Persistence;

public interface IConversationStore
{
    Task<IReadOnlyList<ConversationCheckpoint>> ListAsync(CancellationToken cancellationToken);
    Task<ConversationCheckpoint> CommitAsync(ConversationCheckpoint checkpoint, long expectedRevision, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> RevisionsAsync(Guid id, CancellationToken cancellationToken);
    Task<ConversationCheckpoint> ReadAsync(Guid id, long revision, CancellationToken cancellationToken);
    Task RemoveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken);
}