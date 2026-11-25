using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class SessionPlanTool(IPlanningSessions planning) : ITool
{
    public ToolSpec Spec { get; } = new("session.plan", "Inspect or enter restrictive planning mode; submit a proposal for user review before returning to apply mode. Only a trusted user response can approve the transition. Approval does not grant tool permissions.",
        """
        {"type":"object","properties":{"operation":{"enum":["get","enter","submit"]},"proposal":{"type":"string","minLength":1,"maxLength":16000}},
        "required":["operation"],"additionalProperties":false}
        """, ToolRisk.Write, ["planning", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (operation, _) = Parse(arguments);
        var effects = new List<ToolEffect> { new(operation == "get" ? ToolEffectKind.Read : ToolEffectKind.Write, $"session:{context.SessionId}:planning") };
        if (operation == "submit") effects.Add(new(ToolEffectKind.Ask, $"session:{context.SessionId}:plan-review"));
        return new(new ToolPlan(effects));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, proposal) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var state = operation switch
            {
                "enter" => await planning.EnterAsync(context, cancellationToken),
                "submit" => await planning.RequestApplyAsync(proposal!, context, cancellationToken),
                _ => await planning.ReadAsync(context, cancellationToken),
            };
            return ToolSupport.BoundedText(JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)), context);
        }, cancellationToken);
    }

    private static (string Operation, string? Proposal) Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        var proposal = arguments.String("proposal");
        if (operation is not ("get" or "enter" or "submit")) arguments.Issue("Unknown planning operation.");
        if (operation == "submit" && (string.IsNullOrWhiteSpace(proposal) || proposal.Length > 16000)) arguments.Issue("Submitting requires a nonempty proposal up to 16000 characters.");
        if (operation != "submit" && arguments.Has("proposal")) arguments.Issue("proposal is only valid for submission.");
        arguments.Validate();
        return (operation, proposal);
    }
}