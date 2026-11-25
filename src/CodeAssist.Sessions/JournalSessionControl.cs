using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalSessionControl(IJournalStore journal, Func<string, bool> directoryExists) : ISessionControl
{
    public Task<SessionChange> RenameAsync(Guid sessionId, string title, ToolContext context, CancellationToken cancellationToken) =>
        ChangeAsync(sessionId, "rename", title, context, cancellationToken);

    public Task<SessionChange> RelocateAsync(Guid sessionId, string directory, ToolContext context, CancellationToken cancellationToken) =>
        ChangeAsync(sessionId, "relocate", directory, context, cancellationToken);

    private async Task<SessionChange> ChangeAsync(Guid sessionId, string operation, string value, ToolContext context, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || sessionId != context.SessionId) throw new UnauthorizedAccessException("Session tools may only mutate the current session.");
        if (operation == "rename" && (string.IsNullOrWhiteSpace(value) || value.Length > 120 || value.Any(char.IsControl))) throw new ArgumentException("Invalid session title.");
        if (operation == "relocate" && !directoryExists(value)) throw new IOException("Relocation destination does not exist.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entries = await journal.ReadAsync(sessionId, cancellationToken);
            if (entries.FirstOrDefault() is not SessionStarted) throw new IOException("Session does not exist.");
            JournalEntry entry = operation == "rename" ? new SessionRenamed(sessionId, 0, default, value.Trim()) : new SessionRelocationRequested(sessionId, 0, default, Guid.NewGuid(), value);
            try { await journal.AppendAsync(entry, entries[^1].Seq, cancellationToken); return new(sessionId, operation, value, operation == "relocate"); }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    public async Task<SessionChange?> ApplyPendingAtBoundaryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var entries = await journal.ReadAsync(sessionId, cancellationToken);
        var request = entries.OfType<SessionRelocationRequested>().LastOrDefault();
        if (request is null || entries.OfType<SessionRelocated>().Any(entry => entry.RequestId == request.RequestId)) return null;
        if (!directoryExists(request.Directory)) throw new IOException("Relocation destination no longer exists.");
        await journal.AppendAsync(new SessionRelocated(sessionId, 0, default, request.RequestId, request.Directory), entries[^1].Seq, cancellationToken);
        return new(sessionId, "relocate", request.Directory, false);
    }
}

public sealed class JournalTodoStore(IJournalStore journal) : ITodoStore
{
    public async Task ReplaceAsync(Guid sessionId, IReadOnlyList<TodoItem> items, CancellationToken cancellationToken)
    {
        if (items.Count > 100 || items.Count(item => item.Status == TodoStatus.InProgress) > 1) throw new ArgumentException("Invalid todo list.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var entries = await journal.ReadAsync(sessionId, cancellationToken);
            if (entries.FirstOrDefault() is not SessionStarted) throw new IOException("Session does not exist.");
            var entry = new TodoUpdated(sessionId, 0, default, items.Select(item => new TodoRecord(item.Content, item.Status.ToString())).ToArray());
            try { await journal.AppendAsync(entry, entries[^1].Seq, cancellationToken); return; }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }
}