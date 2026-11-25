using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace CodeAssist.Extensions.Skills;

internal sealed record SkillDocument(SkillSummary Summary, string Body, IReadOnlyList<string> Parameters, string? Route, string? Model, IReadOnlyList<SkillContextRequest> Gather)
{
    internal static SkillDocument Parse(string text, string fallback, string origin)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal);
        var metadata = new YamlMappingNode();
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0 || end > 32_768 || end + 4 < text.Length && text[end + 4] != '\n') throw new IOException("Skill YAML frontmatter is not terminated within 32 KiB.");
            var yaml = new YamlStream();
            try { yaml.Load(new StringReader(end == 3 ? "" : text[4..end])); }
            catch (YamlDotNet.Core.YamlException exception) { throw new IOException("Skill YAML frontmatter is invalid.", exception); }
            if (yaml.Documents.Count > 0)
            {
                if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping) throw new IOException("Skill frontmatter must be one YAML mapping.");
                metadata = mapping;
            }
            text = text[Math.Min(end + 5, text.Length)..];
        }
        var known = new HashSet<string>(["name", "description", "license", "compatibility", "metadata", "audience", "title", "hint", "focus", "parameters", "route", "engine", "gather"], StringComparer.Ordinal);
        if (metadata.Children.Keys.Any(key => key is not YamlScalarNode scalar || scalar.Value is null || !known.Contains(scalar.Value)))
            throw new IOException("Unknown skill frontmatter field. Unsupported execution or permission fields are not ignored.");
        var id = Scalar("name") ?? fallback;
        if (!ValidId(id)) throw new IOException("Invalid skill ID.");
        var description = Scalar("description") ?? "Local skill instructions.";
        var audience = Scalar("audience") switch
        {
            null or "both" => SkillAudience.Both, "human" => SkillAudience.Human, "agent" => SkillAudience.Agent, "none" => SkillAudience.None,
            _ => throw new IOException("Skill audience must be both, human, agent or none."),
        };
        var focus = List("focus");
        foreach (var pattern in focus)
            if (Path.IsPathRooted(pattern) || pattern.Split('/').Any(part => part is ".." or ".") || pattern.Contains('\\')) throw new IOException("Skill focus globs must be workspace-relative portable paths.");
        _ = new GlobMatcher(focus);
        var parameters = List("parameters");
        if (parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Count || parameters.Any(value => value.Length > 32 || !char.IsAsciiLetter(value[0]) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
            throw new IOException("Skill parameter names must be distinct ASCII identifiers.");
        var route = Scalar("route");
        var model = Scalar("engine");
        if (model is not null && route is null) throw new IOException("A skill engine requires an isolated role route.");
        if (route is not null && (!ValidId(route) || route.Length > 80) || model?.Length > 256) throw new IOException("Skill route or engine exceeds its identity bounds.");
        var gather = new List<SkillContextRequest>();
        if (metadata.Children.TryGetValue(new YamlScalarNode("gather"), out var gatherNode))
        {
            if (gatherNode is not YamlSequenceNode requests || requests.Children.Count > 4) throw new IOException("Skill gather requires at most four declared read-only tool calls.");
            foreach (var request in requests.Children)
            {
                if (request is not YamlMappingNode fields || fields.Children.Count != 3 || fields.Children.Keys.Any(key => key is not YamlScalarNode { Value: "slot" or "tool" or "json" }))
                    throw new IOException("Skill gather entries require slot, tool and json fields.");
                var slot = Field("slot");
                var tool = Field("tool");
                var json = Field("json");
                if (!ValidId(slot) || gather.Any(item => item.Slot == slot) || tool.Length > 128 || tool.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_') || json.Length > 8192)
                    throw new IOException("Skill gather names or JSON exceed their bounds.");
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Skill gather JSON must be an object.");
                }
                catch (JsonException exception) { throw new IOException("Skill gather JSON is invalid.", exception); }
                gather.Add(new(slot, tool, json));

                string Field(string key) => fields.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode { Value: { } value }
                    ? value : throw new IOException("Skill gather fields must be literal strings.");
            }
        }
        return new(new(id, description) { Audience = audience, Origin = origin, Title = Scalar("title"), Hint = Scalar("hint"), Focus = focus, Parameters = parameters }, text, parameters, route, model, gather);

        string? Scalar(string key)
        {
            if (!metadata.Children.TryGetValue(new YamlScalarNode(key), out var node)) return null;
            if (node is not YamlScalarNode { Value: { } value } || value.Length > 1024 || value.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
                throw new IOException("Skill metadata requires bounded printable text.");
            return value.Trim();
        }

        IReadOnlyList<string> List(string key)
        {
            if (!metadata.Children.TryGetValue(new YamlScalarNode(key), out var node)) return [];
            if (node is not YamlSequenceNode sequence || sequence.Children.Count > 16) throw new IOException("Skill list metadata requires at most 16 values.");
            return sequence.Children.Select(child => child is YamlScalarNode { Value: { } value } && value.Length is > 0 and <= 256 && !value.Any(char.IsControl)
                ? value : throw new IOException("Skill list values must be nonempty bounded text.")).ToArray();
        }
    }

    internal static bool ValidId(string id) => id.Length is > 0 and <= 80 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}