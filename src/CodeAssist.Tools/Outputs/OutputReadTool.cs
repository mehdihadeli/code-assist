using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class OutputReadTool(IToolOutputStore store) : ITool
{
    public ToolSpec Spec { get; } = new("output.read", "Read retained output owned by this session. Default: numbered lines using offset/limit. For long lines use zero-based UTF-16 characterOffset and characters instead. Storage paths and other sessions are inaccessible.",
        """{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"offset":{"type":"integer","minimum":1},"limit":{"type":"integer","minimum":1,"maximum":2000},"characterOffset":{"type":"integer","minimum":0,"maximum":33554432},"characters":{"type":"integer","minimum":1,"maximum":4000}},"required":["id"],"additionalProperties":false}""", ToolRisk.Read, ["read"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (id, _, _, _, _) = Parse(arguments);
        return new(new ToolPlan([new(ToolEffectKind.Read, $"session:{context.SessionId}:output:{id}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (id, offset, limit, characterOffset, characters) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () => ToolSupport.BoundedText(characterOffset is { } start ? await store.ReadCharactersAsync(context.SessionId, id, start, characters, cancellationToken) : await store.ReadAsync(context.SessionId, id, offset, limit, cancellationToken), context), cancellationToken);
    }

    private static (Guid Id, int Offset, int Limit, int? CharacterOffset, int Characters) Parse(ToolArguments arguments)
    {
        if (!Guid.TryParse(arguments.RequireString("id"), out var id) || id == Guid.Empty) arguments.Issue("Invalid output handle.");
        var offset = arguments.Int("offset", 1) ?? 1;
        var limit = arguments.Int("limit", 1, 2000) ?? 200;
        var characterOffset = arguments.Int("characterOffset", 0, 33554432);
        var characters = arguments.Int("characters", 1, 4000) ?? 2000;
        if (arguments.Has("characters")) characterOffset ??= 0;
        if (characterOffset is not null && (arguments.Has("offset") || arguments.Has("limit"))) arguments.Issue("Choose line paging or character paging, not both.");
        arguments.Validate();
        return (id, offset, limit, characterOffset, characters);
    }
}