using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class AgentDelegateTool(IDelegation delegation, IToolJobs? jobs = null) : ITool
{
    public ToolSpec Spec { get; } = new("agent.delegate", "Run a self-contained task in a role-restricted child session. Resume only a child owned by this parent. Background execution uses host jobs. Available roles: " + string.Join(", ", delegation.Roles),
        """{"type":"object","properties":{"role":{"type":"string","minLength":1},"description":{"type":"string","minLength":1,"maxLength":240},"prompt":{"type":"string","minLength":1,"maxLength":64000},"model":{"type":"string","maxLength":256},"background":{"type":"boolean"},"resume":{"type":"string","format":"uuid"}},"required":["role","description","prompt"],"additionalProperties":false}""",
        ToolRisk.Execute, ["delegate"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments);
        return new(new ToolPlan([new(ToolEffectKind.Exec, $"delegate:{request.Role}")], request.Description));
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments);
        if (!delegation.Roles.Contains(request.Role, StringComparer.OrdinalIgnoreCase)) return ToolResult.Error($"Role '{request.Role}' cannot be delegated to. Available: {string.Join(", ", delegation.Roles)}.");
        if (!arguments.Bool("background")) return await RunAsync(request, context, cancellationToken);
        if (jobs is null) return ToolResult.Error("Background delegation is unavailable: the host has no job service.");
        return await ToolSupport.FromPortAsync(() =>
        {
            var job = jobs.Start(request.Description, token => RunAsync(request, context, token), context, cancellationToken);
            return Task.FromResult(ToolResult.Ok($"Background delegation job: {job.Id}\nRead its final result to obtain the resumable child session id."));
        }, cancellationToken);
    }

    private Task<ToolResult> RunAsync(DelegationRequest request, ToolContext context, CancellationToken cancellationToken) => ToolSupport.FromPortAsync(async () =>
    {
        var result = await delegation.RunAsync(request, context, cancellationToken);
        return ToolSupport.BoundedText($"{result.Text}\n\n[child session: {result.SessionId}]", context) with { IsError = result.IsError };
    }, cancellationToken);

    private static DelegationRequest Parse(ToolArguments arguments)
    {
        var role = arguments.RequireString("role");
        var description = arguments.RequireString("description");
        var prompt = arguments.RequireString("prompt");
        var model = arguments.String("model");
        var resume = arguments.String("resume");
        arguments.Bool("background");
        Guid? resumed = null;
        if (resume is not null)
        {
            if (Guid.TryParse(resume, out var parsed) && parsed != Guid.Empty) resumed = parsed;
            else arguments.Issue("Resume must identify a real child session.");
        }
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(description) || description.Length > 240 || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 64000 || model?.Length > 256) arguments.Issue("Delegation arguments exceed limits or are empty.");
        arguments.Validate();
        return new(role, description, prompt, model, resumed);
    }
}