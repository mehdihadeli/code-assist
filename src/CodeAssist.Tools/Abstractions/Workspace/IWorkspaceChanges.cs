namespace CodeAssist.Tools;

public interface IWorkspaceChanges
{
    Task<IReadOnlyList<WorkspaceChange>> ReadAsync(string state, bool includeDiffs, ToolContext context, CancellationToken cancellationToken);
}