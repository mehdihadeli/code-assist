using CodeAssist.Tools;

namespace CodeAssist.Security;

public sealed class PlanningAuthorization(IToolAuthorization authorization, IPlanningSessions planning) : IToolAuthorization
{
    public async Task<ToolAuthorization> AuthorizeAsync(ToolSpec tool, ToolPlan plan, ToolContext context, CancellationToken cancellationToken)
    {
        PlanningStatus state;
        try { state = await planning.ReadAsync(context, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, "Session planning state could not be verified.");
        }
        var planningControl = tool.Name == "session.plan" && plan.Effects.All(effect =>
            effect.Kind == ToolEffectKind.Read ||
            effect.Kind == ToolEffectKind.Write && effect.Target == $"session:{context.SessionId}:planning" ||
            effect.Kind == ToolEffectKind.Ask && effect.Target == $"session:{context.SessionId}:plan-review");
        if (state.Active && !planningControl && plan.Effects.Any(effect => effect.Kind is ToolEffectKind.Write or ToolEffectKind.Delete or ToolEffectKind.Exec or ToolEffectKind.Net))
            return new(false, "Planning permits only read and question effects until the user approves a proposal.");
        return await authorization.AuthorizeAsync(tool, plan, context, cancellationToken);
    }
}