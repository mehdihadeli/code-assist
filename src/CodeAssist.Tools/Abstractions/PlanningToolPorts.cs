namespace CodeAssist.Tools;

public sealed record PlanningStatus(bool Active, long Revision);

public interface IPlanningSessions
{
    Task<PlanningStatus> ReadAsync(ToolContext context, CancellationToken cancellationToken);
    Task<PlanningStatus> EnterAsync(ToolContext context, CancellationToken cancellationToken);
    Task<PlanningStatus> RequestApplyAsync(string proposal, ToolContext context, CancellationToken cancellationToken);
}