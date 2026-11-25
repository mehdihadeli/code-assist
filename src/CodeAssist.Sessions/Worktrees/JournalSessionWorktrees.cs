using CodeAssist.Persistence;
using System.Collections.Concurrent;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalSessionWorktrees(IJournalStore journal, IWorkspaceBranches branches) : ISessionWorktrees
{
    private readonly ConcurrentDictionary<Guid, string> _active = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    public IReadOnlyCollection<string> ActiveRoots => _active.Values.ToArray();

    public async Task<SessionWorktree> ReadAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var (_, state) = await LoadAsync(context, cancellationToken);
        _active[context.SessionId] = state.Active ? state.Directory! : state.Original;
        return state;
    }

    public async Task<SessionWorktree> EnterAsync(string name, ToolContext context, CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            var (entries, state) = await LoadAsync(context, cancellationToken);
            if (state.Active || state.CleanupPending) throw new InvalidOperationException("Exit the active worktree and finish pending cleanup before entering another.");
            var origin = context.WithWorkspace(state.Original);
            var branch = await branches.CreateAsync(name, origin, cancellationToken);
            var changed = new SessionWorktree(state.Original, branch.Directory, true, false);
            try { await CommitAsync(state, changed, context, cancellationToken); }
            catch
            {
                await branches.RemoveAsync(branch.Directory, false, origin, CancellationToken.None);
                throw;
            }
            _active[context.SessionId] = branch.Directory;
            return changed;
        }
        finally { _mutation.Release(); }
    }

    public async Task<SessionWorktree> ExitAsync(bool remove, ToolContext context, CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken);
        try
        {
            var (entries, state) = await LoadAsync(context, cancellationToken);
            if (!state.Active && !state.CleanupPending) throw new InvalidOperationException("Session has no active worktree or pending cleanup.");
            if (!Directory.Exists(state.Original)) throw new IOException("Original workspace no longer exists; worktree retained.");
            var restored = state with { Active = false, CleanupPending = remove };
            await CommitAsync(state, restored, context, cancellationToken);
            _active[context.SessionId] = state.Original;
            if (!remove) return restored;
            var origin = context.WithWorkspace(state.Original);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (Directory.Exists(state.Directory) || File.Exists(state.Directory) ||
                (await branches.ListAsync(origin, cancellationToken)).Any(branch => string.Equals(Path.GetFullPath(branch.Directory), Path.GetFullPath(state.Directory!), pathComparison)))
                await branches.RemoveAsync(state.Directory!, false, origin, cancellationToken);
            var finished = restored with { CleanupPending = false, Directory = null };
            await CommitAsync(restored, finished, context, CancellationToken.None);
            return finished;
        }
        finally { _mutation.Release(); }
    }

    private async Task CommitAsync(SessionWorktree expected, SessionWorktree changed, ToolContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (entries, current) = await LoadAsync(context, cancellationToken);
            if (current != expected) throw new InvalidOperationException("Worktree state changed concurrently; read it again.");
            try
            {
                await journal.AppendAsync(new WorktreeChanged(context.SessionId, 0, default, changed), entries[^1].Seq, cancellationToken);
                return;
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    private async Task<(IReadOnlyList<JournalEntry> Entries, SessionWorktree State)> LoadAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty) throw new UnauthorizedAccessException("Worktree sessions require a real session ID.");
        var entries = await journal.ReadAsync(context.SessionId, cancellationToken);
        if (entries.FirstOrDefault() is not SessionStarted start) throw new IOException("Session does not exist.");
        var state = entries.OfType<WorktreeChanged>().LastOrDefault()?.State ?? new(Path.GetFullPath(start.Workspace), null, false, false);
        return (entries, state);
    }
}