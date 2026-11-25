namespace CodeAssist.Tools;

public interface IKnowledgeNotes
{
    Task<IReadOnlyList<KnowledgeNote>> ListAsync(string scope, ToolContext context, CancellationToken cancellationToken);
    Task<KnowledgeNote> PutAsync(string scope, Guid? id, long revision, string title, string body, ToolContext context, CancellationToken cancellationToken);
    Task RemoveAsync(string scope, Guid id, long revision, ToolContext context, CancellationToken cancellationToken);
}