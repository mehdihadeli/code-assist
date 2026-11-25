namespace CodeAssist.Tools;

public sealed record WorkspaceBranch(string Directory, string? Branch, bool Managed);

public interface IWorkspaceBranches
{
    Task<string> DestinationAsync(string name, ToolContext context, CancellationToken cancellationToken);
    Task<IReadOnlyList<WorkspaceBranch>> ListAsync(ToolContext context, CancellationToken cancellationToken);
    Task<WorkspaceBranch> CreateAsync(string name, ToolContext context, CancellationToken cancellationToken);
    Task RemoveAsync(string directory, bool force, ToolContext context, CancellationToken cancellationToken);
}

public sealed record SessionChange(Guid SessionId, string Operation, string Value, bool Pending);

public interface ISessionControl
{
    Task<SessionChange> RenameAsync(Guid sessionId, string title, ToolContext context, CancellationToken cancellationToken);
    Task<SessionChange> RelocateAsync(Guid sessionId, string directory, ToolContext context, CancellationToken cancellationToken);
}