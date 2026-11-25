using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class WorkspaceChangesTool(IWorkspaceChanges changes) : ITool
{
    public ToolSpec Spec { get; } = new("workspace.changes", "Inspect staged, unstaged, untracked and conflicted Git paths, optionally with bounded diffs. Read-only live observation, not an atomic repository snapshot; never stages, commits or discards changes.",
        """{"type":"object","properties":{"state":{"enum":["all","staged","unstaged","conflicts"]},"includeDiffs":{"type":"boolean"}},"additionalProperties":false}""", ToolRisk.Execute, ["read", "exec", "git"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        Parse(arguments);
        return new(new ToolPlan([new(ToolEffectKind.Read, "workspace:git-changes"), new(ToolEffectKind.Exec, "git:status-diff")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var state = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var payload = JsonSerializer.SerializeToElement(await changes.ReadAsync(state, arguments.Bool("includeDiffs"), context, cancellationToken));
            return await ToolOutputRetention.CompleteAsync(new(payload.GetRawText(), StructuredContent: payload), context, cancellationToken);
        }, cancellationToken);
    }

    private static string Parse(ToolArguments arguments)
    {
        var state = arguments.String("state") ?? "all";
        if (state is not ("all" or "staged" or "unstaged" or "conflicts")) arguments.Issue("Unknown Git state.");
        arguments.Bool("includeDiffs");
        arguments.Validate();
        return state;
    }
}