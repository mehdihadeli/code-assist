using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public enum JobOperation { List, Read, Stop, Wait }

public sealed class JobTool(IToolJobs jobs, JobOperation operation) : ITool
{
    public ToolSpec Spec { get; } = new(
        $"job.{operation.ToString().ToLowerInvariant()}",
        operation switch
        {
            JobOperation.List => "List this session's background jobs. Completion events should drive notifications; do not repeatedly poll.",
            JobOperation.Read => "Read this session's background job status and its bounded final result. Running jobs have no final result yet.",
            JobOperation.Wait => "Wait for a session-owned job completion event up to 120 seconds without polling. A timeout returns current status and does not stop the job.",
            _ => "Cancel one background job belonging to this session and wait for cancellation cleanup.",
        },
        operation == JobOperation.List ? """{"type":"object","properties":{},"additionalProperties":false}""" :
            operation == JobOperation.Wait ? """{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"seconds":{"type":"integer","minimum":1,"maximum":120}},"required":["id"],"additionalProperties":false}""" :
            """{"type":"object","properties":{"id":{"type":"string","format":"uuid"}},"required":["id"],"additionalProperties":false}""",
        operation == JobOperation.Stop ? ToolRisk.Execute : ToolRisk.Read,
        operation == JobOperation.Stop ? ["exec", "job"] : ["read", "job"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var id = Parse(arguments);
        var effect = operation == JobOperation.Stop ? ToolEffectKind.Exec : ToolEffectKind.Read;
        return new(new ToolPlan([new ToolEffect(effect, $"session:{context.SessionId}:job:{id?.ToString() ?? "*"}")]));
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var id = Parse(arguments);
        return await ToolSupport.FromPortAsync(async () =>
        {
            if (operation == JobOperation.List)
            {
                var items = jobs.List(context.SessionId);
                return ToolSupport.BoundedText(items.Count == 0 ? "No background jobs." : string.Join('\n', items.Select(job => $"{job.Id} | {job.Status} | {job.Description}")), context);
            }
            var job = operation == JobOperation.Wait ? await WaitAsync(id!.Value, arguments.Int("seconds", 1, 120) ?? 30, context, cancellationToken) : operation == JobOperation.Stop
                ? await jobs.StopAsync(context.SessionId, id!.Value, cancellationToken)
                : jobs.Read(context.SessionId, id!.Value);
            if (job is null) return ToolResult.Error("Background job not found in this session.");
            var result = ToolSupport.BoundedText($"Job {job.Id}: {job.Status}\n{job.Result?.Text}", context);
            return result with { IsError = job.Result?.IsError ?? false, Attachments = job.Result?.Attachments };
        }, cancellationToken);
    }

    private async Task<ToolJob?> WaitAsync(Guid id, int seconds, ToolContext context, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(ToolJob job)
        {
            if (job.Id == id && job.SessionId == context.SessionId) completion.TrySetResult(job);
        }
        jobs.Completed += Completed;
        try
        {
            var current = jobs.Read(context.SessionId, id);
            if (current is null || current.Status != ToolJobStatus.Running) return current;
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(seconds), cancellationToken); }
            catch (TimeoutException) { return jobs.Read(context.SessionId, id); }
        }
        finally { jobs.Completed -= Completed; }
    }

    private Guid? Parse(ToolArguments arguments)
    {
        Guid? id = null;
        if (operation != JobOperation.List)
        {
            if (Guid.TryParse(arguments.RequireString("id"), out var parsed)) id = parsed;
            else arguments.Issue("'id' must be a job id.");
        }
        if (operation == JobOperation.Wait) arguments.Int("seconds", 1, 120);
        arguments.Validate();
        return id;
    }
}