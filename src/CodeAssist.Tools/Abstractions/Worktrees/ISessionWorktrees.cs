using CodeAssist.Persistence;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public interface ISessionWorktrees
{
    IReadOnlyCollection<string> ActiveRoots { get; }
    Task<SessionWorktree> ReadAsync(ToolContext context, CancellationToken cancellationToken);
    Task<SessionWorktree> EnterAsync(string name, ToolContext context, CancellationToken cancellationToken);
    Task<SessionWorktree> ExitAsync(bool remove, ToolContext context, CancellationToken cancellationToken);
}