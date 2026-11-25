using System.Collections.Concurrent;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed class WorkspaceToolDispatcher(IToolDispatcher inner, ISessionWorktrees worktrees, IToolOutputStore? outputs = null) : IToolDispatcher
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessions = new();
    private readonly AsyncLocal<Guid?> _current = new();
    public ToolSpec? Describe(string name, ToolContext context) => inner.Describe(name, context);

    public async Task<ToolResult> InvokeAsync(string name, string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        context = context.WithWorkspace(context.Workspace, outputs);
        if (_current.Value == context.SessionId)
        {
            if (name == "workspace.worktree") return ToolResult.Error("Worktree lifecycle changes must be top-level calls, not nested flow steps.");
            var nestedState = await worktrees.ReadAsync(context, cancellationToken);
            return await inner.InvokeAsync(name, arguments, context.WithWorkspace(nestedState.Active ? nestedState.Directory! : nestedState.Original), cancellationToken);
        }
        var gate = _sessions.GetOrAdd(context.SessionId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        var previous = _current.Value;
        _current.Value = context.SessionId;
        try
        {
            var state = await worktrees.ReadAsync(context, cancellationToken);
            return await inner.InvokeAsync(name, arguments, context.WithWorkspace(state.Active ? state.Directory! : state.Original), cancellationToken);
        }
        finally { _current.Value = previous; gate.Release(); }
    }
}