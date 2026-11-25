using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class TeamManageTool(ITeamHub teams) : ITool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ToolSpec Spec { get; } = new("team.manage", "Create or inspect a durable team, invite/revoke sessions, join/leave or close. Only the leader manages invitations and closes; invitees join themselves. Closing requires all teammates to leave. Audit history is retained.",
        """{"type":"object","properties":{"operation":{"enum":["create","get","invite","revoke","join","leave","close"]},"team":{"type":"string","format":"uuid"},"name":{"type":"string","minLength":1,"maxLength":80},"revision":{"type":"integer","minimum":1},"member":{"type":"string","format":"uuid"}},"required":["operation"],"additionalProperties":false}""",
        ToolRisk.Write, ["team", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments);
        return new(new ToolPlan([new(request.Operation == "get" ? ToolEffectKind.Read : ToolEffectKind.Write,
            request.Operation == "create" ? $"session:{context.SessionId}:new-team" : $"team:{request.Team}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var result = request.Operation switch
            {
                "create" => await teams.CreateAsync(request.Name!, context, cancellationToken),
                "get" => await teams.GetAsync(request.Team, context, cancellationToken),
                _ => await teams.ChangeAsync(request.Team, request.Revision, request.Mutation, request.Member, context, cancellationToken),
            };
            return ToolSupport.BoundedText(JsonSerializer.Serialize(result, Json), context);
        }, cancellationToken);
    }

    private static TeamManagementRequest Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("create" or "get" or "invite" or "revoke" or "join" or "leave" or "close")) arguments.Issue("Unknown team operation.");
        var team = operation == "create" ? Guid.Empty : TeamArguments.Id(arguments, "team");
        var name = arguments.String("name");
        if (operation == "create" && (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))) arguments.Issue("Creation needs a nonempty name up to 80 characters without controls.");
        var revision = arguments.Int("revision", 1) ?? 0;
        if (operation is not ("create" or "get") && revision == 0) arguments.Issue("Mutation requires current revision.");
        Guid? member = operation is "invite" or "revoke" ? TeamArguments.Id(arguments, "member") : null;
        if (operation is not ("invite" or "revoke") && arguments.Has("member")) arguments.Issue("Only invite/revoke accepts member; joining and leaving use current session identity.");
        var mutation = operation switch { "invite" => TeamMutation.Invite, "revoke" => TeamMutation.Revoke, "join" => TeamMutation.Join, "leave" => TeamMutation.Leave, _ => TeamMutation.Close };
        arguments.Validate();
        return new(operation, team, name, revision, mutation, member);
    }
}