using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

public sealed class JournalWorkLedger(IJournalStore journal) : IWorkLedger
{
    public async Task<IReadOnlyList<WorkItem>> ListAsync(ToolContext context, CancellationToken cancellationToken)
    {
        var (_, items) = await LoadAsync(context, cancellationToken);
        return items.Values.OrderBy(item => item.Id).ToArray();
    }

    public Task<WorkItem> CreateAsync(WorkDraft draft, ToolContext context, CancellationToken cancellationToken) =>
        ChangeAsync(Guid.NewGuid(), null, draft, WorkState.Queued, false, context, cancellationToken);

    public Task<WorkItem> ReplaceAsync(Guid id, int revision, WorkDraft draft, WorkState state, ToolContext context, CancellationToken cancellationToken) =>
        ChangeAsync(id, revision, draft, state, false, context, cancellationToken);

    public async Task RemoveAsync(Guid id, int revision, ToolContext context, CancellationToken cancellationToken) =>
        await ChangeAsync(id, revision, null, WorkState.Cancelled, true, context, cancellationToken);

    private async Task<WorkItem> ChangeAsync(Guid id, int? revision, WorkDraft? draft, WorkState state, bool remove, ToolContext context, CancellationToken cancellationToken)
    {
        if (draft is not null) Validate(draft);
        if (!Enum.IsDefined(state)) throw new ArgumentException("Unknown work state.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var (sequence, items) = await LoadAsync(context, cancellationToken);
            items.TryGetValue(id, out var current);
            if (revision is not null && (current is null || current.Revision != revision))
                throw new InvalidOperationException("Work item missing or revision stale; read it again before updating.");
            if (revision is null && items.Count >= 500) throw new InvalidOperationException("Session work ledger is limited to 500 items.");
            WorkItem changed;
            if (remove)
            {
                if (current!.State == WorkState.Active || items.Values.Any(item => item.Requires.Contains(id)))
                    throw new InvalidOperationException("Cancel active work and remove dependent links before removal.");
                changed = current with { Revision = checked(current.Revision + 1) };
                items.Remove(id);
            }
            else
            {
                changed = new WorkItem(id, draft!.Title.Trim(), draft.Detail, string.IsNullOrWhiteSpace(draft.Owner) ? null : draft.Owner.Trim(),
                    state, draft.Requires.ToArray(), checked((current?.Revision ?? 0) + 1));
                items[id] = changed;
                ValidateGraph(items);
            }
            try
            {
                await journal.AppendAsync(new WorkItemChanged(context.SessionId, 0, default, changed, remove), sequence, cancellationToken);
                return changed;
            }
            catch (JournalConflictException) when (attempt < 3) { }
        }
        throw new JournalConflictException();
    }

    private async Task<(long Sequence, Dictionary<Guid, WorkItem> Items)> LoadAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty) throw new UnauthorizedAccessException("Work ledger requires a real session.");
        var entries = await journal.ReadAsync(context.SessionId, cancellationToken);
        if (entries.FirstOrDefault() is not SessionStarted) throw new IOException("Session does not exist.");
        var items = new Dictionary<Guid, WorkItem>();
        foreach (var entry in entries.OfType<WorkItemChanged>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Removed) items.Remove(entry.Item.Id);
            else items[entry.Item.Id] = entry.Item;
        }
        return (entries[^1].Seq, items);
    }

    private static void Validate(WorkDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200 || draft.Title.Any(char.IsControl) ||
            draft.Detail.Length > 16000 || draft.Owner?.Length > 120 || draft.Owner?.Any(char.IsControl) == true ||
            draft.Requires.Count > 100 || draft.Requires.Any(id => id == Guid.Empty) || draft.Requires.Distinct().Count() != draft.Requires.Count)
            throw new ArgumentException("Invalid work title, detail, owner or prerequisites.");
    }

    private static void ValidateGraph(Dictionary<Guid, WorkItem> items)
    {
        var visited = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("Work prerequisites contain a cycle.");
            foreach (var dependency in items[id].Requires)
            {
                if (!items.ContainsKey(dependency)) throw new InvalidOperationException("Prerequisites must exist in the same session ledger.");
                Visit(dependency);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var item in items.Values)
        {
            Visit(item.Id);
            if (item.State is WorkState.Active or WorkState.Done && item.Requires.Any(id => items[id].State != WorkState.Done))
                throw new InvalidOperationException("Active or completed work requires every prerequisite to be done.");
        }
    }
}