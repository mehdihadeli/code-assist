namespace CodeAssist.Tools;

public interface IWorkspaceRetrieval
{
    bool SupportsSemantic { get; }
    string? EmbeddingDestination { get; }
    Task<RetrievalResult> SearchAsync(string query, string strategy, string path, int limit, ToolContext context, CancellationToken cancellationToken);
}