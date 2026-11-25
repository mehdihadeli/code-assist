using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class WorkspaceBranchTool(IWorkspaceBranches branches) : ITool
{
    public ToolSpec Spec { get; } = new("workspace.branch", "List, create or remove CodeAssist-managed Git worktrees. Creation does not move the session. Removal refuses active roots and dirty worktrees unless force is explicitly set; forced removal discards files.",
        """
        {"type":"object","properties":{"operation":{"type":"string","enum":["list","create","remove"]},
         "name":{"type":"string"},"directory":{"type":"string"},"force":{"type":"boolean"}},"required":["operation"],"additionalProperties":false}
        """, ToolRisk.Execute, ["workspace", "exec", "write"]);

    public async ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, name, directory, force) = Parse(arguments);
        if (operation == "list") return new([new(ToolEffectKind.Read, context.Workspace)]);
        var target = ToolSupport.Resolve(operation == "create" ? await branches.DestinationAsync(name!, context, cancellationToken) : directory!, context);
        return new([new(ToolEffectKind.Exec, $"git worktree {operation}"), ToolSupport.Effect(operation == "create" ? ToolEffectKind.Write : ToolEffectKind.Delete, target)],
            force ? $"FORCED removal discards modified and untracked files: {target.Full}" : $"{operation} worktree: {target.Full}");
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, name, directory, force) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            if (operation == "list") return ToolSupport.BoundedText(JsonSerializer.Serialize(await branches.ListAsync(context, cancellationToken)), context);
            if (operation == "create") return ToolSupport.BoundedText(JsonSerializer.Serialize(await branches.CreateAsync(name!, context, cancellationToken)), context);
            await branches.RemoveAsync(ToolSupport.Resolve(directory!, context).Full, force, context, cancellationToken);
            return ToolResult.Ok("Worktree removed.");
        }, cancellationToken);
    }

    private static (string Operation, string? Name, string? Directory, bool Force) Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        var name = arguments.String("name");
        var directory = arguments.String("directory");
        var force = arguments.Bool("force");
        if (operation is not ("list" or "create" or "remove")) arguments.Issue("Unknown worktree operation.");
        if (operation == "create" && (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !char.IsAsciiLetterOrDigit(name[0]) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
            arguments.Issue("Creation needs a safe 1-64 character name (letters, digits, hyphens, underscores).");
        if (operation == "remove" && string.IsNullOrWhiteSpace(directory)) arguments.Issue("Removal requires directory.");
        if (force && operation != "remove") arguments.Issue("force is only valid for removal.");
        arguments.Validate();
        return (operation, name, directory, force);
    }
}

public sealed class SessionManageTool(ISessionControl sessions) : ITool
{
    public ToolSpec Spec { get; } = new("session.manage", "Rename a session or request workspace relocation at the next safe boundary. Omit sessionId for current session. A pending relocation does not change this call's workspace.",
        """
        {"type":"object","properties":{"operation":{"type":"string","enum":["rename","relocate"]},
         "sessionId":{"type":"string","format":"uuid"},"title":{"type":"string"},"directory":{"type":"string"}},"required":["operation"],"additionalProperties":false}
        """, ToolRisk.Write, ["session", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, session, value) = Parse(arguments, context);
        var effects = new List<ToolEffect> { new(ToolEffectKind.Write, $"session:{session}:{operation}") };
        if (operation == "relocate") effects.Add(ToolSupport.Effect(ToolEffectKind.Read, ToolSupport.Resolve(value, context)));
        return new(new ToolPlan(effects, $"{operation} session {session}: {value}"));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, session, value) = Parse(arguments, context);
        return ToolSupport.FromPortAsync(async () =>
        {
            var result = operation == "rename" ? await sessions.RenameAsync(session, value, context, cancellationToken) :
                await sessions.RelocateAsync(session, ToolSupport.Resolve(value, context).Full, context, cancellationToken);
            return ToolSupport.BoundedText(JsonSerializer.Serialize(result), context);
        }, cancellationToken);
    }

    private static (string Operation, Guid Session, string Value) Parse(ToolArguments arguments, ToolContext context)
    {
        var operation = arguments.RequireString("operation");
        var id = arguments.String("sessionId");
        var session = context.SessionId;
        if (id is not null && !Guid.TryParse(id, out session)) arguments.Issue("sessionId must be a UUID.");
        if (operation is not ("rename" or "relocate")) arguments.Issue("Unknown session operation.");
        var value = arguments.RequireString(operation == "rename" ? "title" : "directory").Trim();
        if (value.Length == 0 || value.Any(char.IsControl) || (operation == "rename" && value.Length > 120)) arguments.Issue("Session value must be nonempty, contain no control characters, and titles must fit 120 characters.");
        arguments.Validate();
        return (operation, session, value);
    }
}