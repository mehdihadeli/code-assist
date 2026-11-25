using CodeAssist.Persistence;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed record WorkDraft(string Title, string Detail, string? Owner, IReadOnlyList<Guid> Requires);

public interface IWorkLedger
{
    Task<IReadOnlyList<WorkItem>> ListAsync(ToolContext context, CancellationToken cancellationToken);
    Task<WorkItem> CreateAsync(WorkDraft draft, ToolContext context, CancellationToken cancellationToken);
    Task<WorkItem> ReplaceAsync(Guid id, int revision, WorkDraft draft, WorkState state, ToolContext context, CancellationToken cancellationToken);
    Task RemoveAsync(Guid id, int revision, ToolContext context, CancellationToken cancellationToken);
}