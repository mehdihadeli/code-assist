using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class TeamMailTool(ITeamHub teams) : ITool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ToolSpec Spec { get; } = new("team.mail", "Send untrusted direct/broadcast text or typed cooperative shutdown requests/responses. Read a private cursor inbox or acknowledge delivery. Stable call IDs make retries idempotent. Acceptance never grants permission; teammates must finish work and leave before close.",
        """{"type":"object","properties":{"operation":{"enum":["send","read","ack"]},"team":{"type":"string","format":"uuid"},"recipient":{"type":"string","format":"uuid"},"broadcast":{"type":"boolean"},"topic":{"type":"string","maxLength":200},"text":{"type":"string","maxLength":16000},"kind":{"enum":["text","shutdown.request","shutdown.accept","shutdown.reject"]},"replyTo":{"type":"string","format":"uuid"},"letter":{"type":"string","format":"uuid"},"after":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100}},"required":["operation","team"],"additionalProperties":false}""",
        ToolRisk.Write, ["team", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments);
        return new(new ToolPlan([new(request.Operation == "read" ? ToolEffectKind.Read : ToolEffectKind.Write, $"team:{request.Team}:mail")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            object result;
            if (request.Operation == "send") result = await teams.SendAsync(request.Team, request.Draft!, context, cancellationToken);
            else if (request.Operation == "read") result = await teams.ReadAsync(request.Team, request.After, request.Limit, context, cancellationToken);
            else
            {
                await teams.AcknowledgeAsync(request.Team, request.Letter, context, cancellationToken);
                result = new { acknowledged = request.Letter };
            }
            return ToolSupport.BoundedText(JsonSerializer.Serialize(result, Json), context);
        }, cancellationToken);
    }

    private static TeamMailRequest Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("send" or "read" or "ack")) arguments.Issue("Unknown mail operation.");
        var team = TeamArguments.Id(arguments, "team");
        var letter = operation == "ack" ? TeamArguments.Id(arguments, "letter") : Guid.Empty;
        LetterDraft? draft = null;
        if (operation == "send")
        {
            Guid? recipient = arguments.Has("recipient") ? TeamArguments.Id(arguments, "recipient") : null;
            Guid? reply = arguments.Has("replyTo") ? TeamArguments.Id(arguments, "replyTo") : null;
            var broadcast = arguments.Bool("broadcast");
            var topic = arguments.RequireString("topic");
            var text = arguments.RequireString("text");
            if (broadcast == (recipient is not null)) arguments.Issue("Use exactly one recipient or broadcast=true.");
            if (string.IsNullOrWhiteSpace(topic) || topic.Length > 200 || topic.Any(char.IsControl) || string.IsNullOrWhiteSpace(text) || text.Length > 16000) arguments.Issue("Mail needs bounded, nonempty topic/text without controls in the topic.");
            var kind = arguments.String("kind") ?? "text";
            if (kind is not ("text" or "shutdown.request" or "shutdown.accept" or "shutdown.reject")) arguments.Issue("Unknown message kind.");
            if (kind is "shutdown.accept" or "shutdown.reject" && reply is null) arguments.Issue("Shutdown responses require replyTo.");
            draft = new(recipient, broadcast, topic, text, reply, kind);
        }
        var after = arguments.Int("after", 0) ?? 0;
        var limit = arguments.Int("limit", 1, 100) ?? 20;
        arguments.Validate();
        return new(operation, team, draft, letter, after, limit);
    }
}