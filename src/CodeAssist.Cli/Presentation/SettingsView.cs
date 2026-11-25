using System.Text.Json.Nodes;
using CodeAssist.Configuration;
using Spectre.Console;

namespace CodeAssist.Cli.Presentation;

public static class SettingsView
{
    public static string EnvironmentName(IReadOnlyList<string> segments) =>
        string.Join("__", segments.Select(s => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s));

    public static string Display(JsonNode? value) => value switch
    {
        null => "(unset)",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    public static string TierLabel(SettingOrigin origin)
    {
        if (origin.Effective is null) return "default";
        var tier = SettingsJson.CamelCase(origin.Tier.ToString());
        return origin.Tier is SettingsTier.Environment or SettingsTier.Flags ? tier : $"{tier}: {FileName(origin.Source)}";
    }

    private static string FileName(string source) =>
        Path.IsPathRooted(source) ? Path.Combine(Path.GetFileName(Path.GetDirectoryName(source) ?? ""), Path.GetFileName(source)) : source;

    public static void RenderLeaves(IAnsiConsole console, IEnumerable<SettingLeaf> leaves, bool showSource)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Key").AddColumn("Value");
        if (showSource) table.AddColumn("Source");

        foreach (var leaf in leaves)
        {
            var value = Markup.Escape(Truncate(Display(leaf.Value), 80));
            var key = Markup.Escape(leaf.Path);
            var overridden = leaf.Origin.Effective is not null;
            var row = new List<string> { overridden ? $"[bold]{key}[/]" : key, value };
            if (showSource) row.Add($"[grey]{Markup.Escape(TierLabel(leaf.Origin))}[/]");
            table.AddRow(row.ToArray());
        }
        console.Write(table);
    }

    public static void RenderCandidates(IAnsiConsole console, SettingOrigin origin)
    {
        if (origin.Candidates.Count == 0)
        {
            console.MarkupLine("[grey]Only the built-in default applies.[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Tier").AddColumn("Source").AddColumn("Value").AddColumn("Result");
        foreach (var c in origin.Candidates)
        {
            var result = c.Accepted ? (c == origin.Effective ? "[green]effective[/]" : "[grey]overridden[/]") : $"[yellow]rejected[/] [grey]{Markup.Escape(c.Reason ?? "")}[/]";
            table.AddRow(SettingsJson.CamelCase(c.Tier.ToString()), Markup.Escape(c.Source), Markup.Escape(Truncate(c.Value, 60)), result);
        }
        console.Write(table);
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
