using CodeAssist.Tools;
using CodeAssist.Contracts;
using System.Text.Json;

namespace CodeAssist.Extensions.Skills;

public sealed class SkillLoadTool(ISkillCatalog skills) : ITool
{
    public ToolSpec Spec => new(
        "skill.load", "Load reusable guidance, list skills, or read one private bundle resource. Never grants permission or executes scripts. Use list=true for the current scoped index. Available: " +
        SkillListing.Render(skills.Index),
        """{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":240},"list":{"type":"boolean"},"path":{"type":"string","minLength":1,"maxLength":1024},"input":{"type":"string","maxLength":4000},"args":{"type":"array","maxItems":16,"items":{"type":"string","maxLength":4000}},"values":{"type":"object","maxProperties":16,"additionalProperties":{"type":"string","maxLength":4000}},"files":{"type":"array","maxItems":64,"items":{"type":"string","maxLength":1024}}},"additionalProperties":false}""",
        ToolRisk.Read, ["read", "interact"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (id, path, _, list) = Parse(arguments);
        return new(new ToolPlan([new(ToolEffectKind.Read, list ? "skill:index" : $"skill:{id}:{path ?? "SKILL.md"}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (id, path, invocation, list) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            if (list) return ToolSupport.BoundedText(JsonSerializer.Serialize(skills.Describe(context)), context);
            if (path is not null)
            {
                var resource = await skills.ReadFileAsync(id, path, context, cancellationToken);
                return resource is null ? ToolResult.Error("Skill resource not found.") : ToolSupport.BoundedText(resource, context);
            }
            var skill = await skills.InvokeAsync(id, invocation, context, cancellationToken);
            if (skill is null) return ToolResult.Error($"No skill '{id}'. Available: {string.Join(", ", skills.Index.Select(item => item.Id))}.");
            var files = skill.Files.Count == 0 ? "" : $"\n\nBundled files: {string.Join(", ", skill.Files)}";
            var route = skill.Route is null ? "" : $"\n\nIsolated role route: {skill.Route}. Use skill.run for execution; this read starts no child.";
            return ToolSupport.BoundedText($"Skill {skill.Id} (guidance, never authority)\n\n{skill.Body}{files}{route}", context);
        }, cancellationToken);
    }

    internal static (string Id, string? Path, SkillInvocation Invocation, bool List) Parse(ToolArguments arguments)
    {
        var list = arguments.Bool("list");
        var id = arguments.String("id") ?? "";
        var path = arguments.String("path");
        var input = arguments.String("input");
        if (!list && !SkillListing.ValidId(id)) arguments.Issue("Invalid skill ID.");
        if (path is not null && (string.IsNullOrWhiteSpace(path) || path.Length > 1024)) arguments.Issue("Invalid skill resource path.");
        if (input?.Length > 4000) arguments.Issue("Skill input exceeds 4000 characters.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.Has("values"))
        {
            var node = arguments.Json.GetProperty("values");
            if (node.ValueKind != JsonValueKind.Object) arguments.Issue("Skill values must be a JSON string map.");
            else foreach (var pair in node.EnumerateObject())
                if (values.Count >= 16 || pair.Value.ValueKind != JsonValueKind.String || !values.TryAdd(pair.Name, pair.Value.GetString()!)) arguments.Issue("Skill values require at most 16 distinct string properties.");
        }
        var positional = Strings("args", 16, 4000);
        var files = Strings("files", 64, 1024);
        if (path is not null && (input is not null || positional.Count > 0 || values.Count > 0 || files.Count > 0)) arguments.Issue("Invocation values apply only to skill instructions.");
        if (list && (id.Length > 0 || path is not null || input is not null || positional.Count > 0 || values.Count > 0 || files.Count > 0)) arguments.Issue("Skill index listing cannot be combined with invocation fields.");
        arguments.Validate();
        return (id, path, new(Input: input ?? "", Arguments: positional, Values: values, Files: files), list);

        IReadOnlyList<string> Strings(string key, int maximum, int length)
        {
            if (!arguments.Has(key)) return [];
            var array = arguments.Json.GetProperty(key);
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) { arguments.Issue("Skill argument array exceeds its bounds."); return []; }
            return array.EnumerateArray().Select(value =>
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= length) return value.GetString()!;
                arguments.Issue("Skill argument arrays require bounded strings.");
                return "";
            }).ToArray();
        }
    }
}