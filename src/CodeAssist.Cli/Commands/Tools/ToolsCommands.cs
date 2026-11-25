using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Tools;

public sealed class ToolsListCommand(CliContext cli) : CliCommand<OutputSettings>(cli)
{
    protected override Task<int> RunAsync(CommandContext context, OutputSettings options, CancellationToken cancellation)
    {
        var settings = Cli.Snapshot.Settings;
        var client = Cli.Api;

        // The selected role narrows the list exactly as it does for a session.
        settings.Roles.TryGetValue(settings.Agent.Role, out var role);
        var tools = client.ListTools(role?.Tools.Allow, role?.Tools.Deny);

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(tools.Select(t => new { t.Name, t.Description, t.Risk, t.ReadOnly, t.Tags }).ToArray());
            return Task.FromResult(ExitCodes.Success);
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Tool").AddColumn("Risk").AddColumn("Tags").AddColumn("Description");
        foreach (var tool in tools)
            table.AddRow(Markup.Escape(tool.Name), tool.Risk, Markup.Escape(string.Join(", ", tool.Tags)), Markup.Escape(FirstSentence(tool.Description)));
        Cli.Console.Write(table);
        Cli.Console.MarkupLine($"[grey]Role [bold]{Markup.Escape(settings.Agent.Role)}[/]. Interaction and delegation tools appear when a session provides them.[/]");
        return Task.FromResult(ExitCodes.Success);
    }

    internal static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? text : text[..(end + 1)];
    }
}

public sealed class ToolsShowSettings : OutputSettings
{
    [CommandArgument(0, "<tool>"), Description("Tool name, for example fs.edit.")]
    public string Tool { get; init; } = "";
}

public sealed class ToolsShowCommand(CliContext cli) : CliCommand<ToolsShowSettings>(cli)
{
    protected override Task<int> RunAsync(CommandContext context, ToolsShowSettings options, CancellationToken cancellation)
    {
        var client = Cli.Api;
        var tool = client.GetTool(options.Tool);
        if (tool is null)
        {
            Cli.Console.MarkupLine($"[red]error[/] no tool named '{Markup.Escape(options.Tool)}'. Run [bold]code-assist tools list[/].");
            return Task.FromResult(ExitCodes.UsageOrConfig);
        }

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(tool);
            return Task.FromResult(ExitCodes.Success);
        }

        Cli.Console.MarkupLine($"[bold]{Markup.Escape(tool.Name)}[/]  [grey]{tool.Risk}, tags: {Markup.Escape(string.Join(", ", tool.Tags))}[/]");
        Cli.Console.WriteLine(tool.Description);
        Cli.Console.WriteLine();
        Cli.Console.WriteLine(tool.InputSchema);
        return Task.FromResult(ExitCodes.Success);
    }
}
