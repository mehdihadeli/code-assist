using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class JobNoticesTool(IJobNotices notices) : ITool
{
    public ToolSpec Spec { get; } = new("job.notices", "Read up to 100 unacknowledged durable background completion notices, or acknowledge one after handling it. Running jobs are not recovered after restart.",
        """{"type":"object","properties":{"action":{"type":"string","enum":["list","ack"]},"id":{"type":"string","format":"uuid"}},"required":["action"],"additionalProperties":false}""", ToolRisk.Write, ["read", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (action, _) = Parse(arguments);
        return new(new ToolPlan([new(action == "list" ? ToolEffectKind.Read : ToolEffectKind.Write, $"session:{context.SessionId}:job-notices")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (action, id) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            if (action == "ack")
            {
                await notices.AcknowledgeAsync(context.SessionId, id, cancellationToken);
                return ToolResult.Ok("Completion notice acknowledged.");
            }
            return await ToolOutputRetention.CompleteAsync(ToolResult.Ok(JsonSerializer.Serialize(await notices.ReadAsync(context.SessionId, cancellationToken))), context, cancellationToken);
        }, cancellationToken);
    }

    private static (string Action, Guid Id) Parse(ToolArguments arguments)
    {
        var action = arguments.RequireString("action");
        var id = Guid.Empty;
        if (action is not ("list" or "ack")) arguments.Issue("Unknown notice action.");
        if (action == "ack" && (!Guid.TryParse(arguments.RequireString("id"), out id) || id == Guid.Empty)) arguments.Issue("Acknowledgement requires a job UUID.");
        arguments.Validate();
        return (action, id);
    }
}