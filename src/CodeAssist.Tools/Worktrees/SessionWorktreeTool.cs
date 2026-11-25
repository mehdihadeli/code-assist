using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class SessionWorktreeTool(ISessionWorktrees sessions, IWorkspaceBranches branches) : ITool
{
    public ToolSpec Spec { get; } = new("workspace.worktree", "Get, enter or exit the current session's managed Git worktree. Exit restores the original workspace. Set remove=true to request guarded cleanup; dirty files and unique commits are never discarded.",
        """{"type":"object","properties":{"operation":{"enum":["get","enter","exit"]},"name":{"type":"string","minLength":1,"maxLength":64},"remove":{"type":"boolean"}},"required":["operation"],"additionalProperties":false}""",
        ToolRisk.Write, ["workspace", "write", "exec"]);

    public async ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var operation = Parse(arguments);
        var state = await sessions.ReadAsync(context, cancellationToken);
        if (operation == "get") return new([new(ToolEffectKind.Read, $"session:{context.SessionId}:worktree")]);
        var effects = new List<ToolEffect> { new(ToolEffectKind.Write, $"session:{context.SessionId}:worktree"), new(ToolEffectKind.Exec, "git:worktree") };
        if (operation == "enter") effects.Add(ToolSupport.Effect(ToolEffectKind.Write, ToolSupport.Resolve(await branches.DestinationAsync(arguments.RequireString("name"), context.WithWorkspace(state.Original), cancellationToken), context)));
        else if (arguments.Bool("remove") && state.Directory is not null) effects.Add(ToolSupport.Effect(ToolEffectKind.Delete, ToolSupport.Resolve(state.Directory, context)));
        return new(effects);
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var operation = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var state = operation switch
            {
                "enter" => await sessions.EnterAsync(arguments.RequireString("name"), context, cancellationToken),
                "exit" => await sessions.ExitAsync(arguments.Bool("remove"), context, cancellationToken),
                _ => await sessions.ReadAsync(context, cancellationToken),
            };
            return ToolSupport.BoundedText(JsonSerializer.Serialize(state), context);
        }, cancellationToken);
    }

    private static string Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("get" or "enter" or "exit")) arguments.Issue("Unknown worktree operation.");
        if (operation == "enter")
        {
            var name = arguments.RequireString("name");
            if (name.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(name[0]) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')) arguments.Issue("Invalid worktree name.");
        }
        else if (arguments.Has("name")) arguments.Issue("Only enter accepts a name.");
        if (operation != "exit" && arguments.Has("remove")) arguments.Issue("Only exit accepts remove.");
        arguments.Validate();
        return operation;
    }
}