using System.ComponentModel;
using System.Text.Json.Nodes;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigExplainCommand(CliContext cli) : CliCommand<ConfigExplainCommand.Options>(cli)
{
    public sealed class Options : OutputSettings
    {
        [CommandArgument(0, "<PATH>")]
        [Description("Setting or section path, for example agent.maxTurns or providers.")]
        public string Path { get; init; } = "";
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        if (!PathArgument.TryResolve(options.Path, out var resolved, out var error)) throw new UsageException(error!);

        var node = resolved!.Node;
        var snapshot = Cli.Snapshot;
        var current = SettingsSnapshot.RedactValue(snapshot.Find(resolved.Segments), node);
        var fallback = SettingsSnapshot.RedactValue(SettingsSnapshot.FindIn(SettingsJson.DefaultsTree(), resolved.Segments), node);
        var origin = snapshot.OriginOf(resolved.Text);

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(new JsonObject
            {
                ["path"] = resolved.Text,
                ["type"] = ScalarConverter.Describe(node),
                ["description"] = node.Description,
                ["trust"] = SettingsJson.CamelCase(node.Trust.ToString()),
                ["restartRequired"] = node.Reload == ReloadKind.RestartRequired,
                ["environmentVariable"] = SettingsView.EnvironmentName(resolved.Segments),
                ["default"] = fallback?.DeepClone(),
                ["value"] = current?.DeepClone(),
                ["source"] = SettingsView.TierLabel(origin),
            });
            return Task.FromResult(ExitCodes.Success);
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[bold]Key[/]", Markup.Escape(resolved.Text));
        grid.AddRow("[bold]Type[/]", Markup.Escape(ScalarConverter.Describe(node)));
        if (node.Description is not null) grid.AddRow("[bold]About[/]", Markup.Escape(node.Description));
        grid.AddRow("[bold]Who can set[/]", Markup.Escape(Who(node)));
        if (node.Strict != StrictDirection.None) grid.AddRow("[bold]Project limit[/]", Markup.Escape(StrictText(node)));
        if (node.Reload == ReloadKind.RestartRequired) grid.AddRow("[bold]Reload[/]", "restart required");
        if (node.IsSecret) grid.AddRow("[bold]Secret[/]", "never stored in files; use the environment variable");
        grid.AddRow("[bold]Environment[/]", Markup.Escape(SettingsView.EnvironmentName(resolved.Segments)));
        grid.AddRow("[bold]Default[/]", Markup.Escape(SettingsView.Truncate(SettingsView.Display(fallback), 100)));
        grid.AddRow("[bold]Current[/]", Markup.Escape(SettingsView.Truncate(SettingsView.Display(current), 100)));
        grid.AddRow("[bold]Source[/]", Markup.Escape(SettingsView.TierLabel(origin)));
        Cli.Console.Write(new Panel(grid).Header("config explain").Border(BoxBorder.Rounded));

        if (node.Kind == KeyKind.Object)
        {
            var children = new Table().Border(TableBorder.Rounded).AddColumn("Key").AddColumn("Type").AddColumn("About");
            foreach (var (name, child) in node.Children)
                children.AddRow(Markup.Escape(name), Markup.Escape(Kind(child)), Markup.Escape(child.Description ?? ""));
            Cli.Console.Write(children);
        }

        if (node.Kind is KeyKind.Scalar or KeyKind.List) SettingsView.RenderCandidates(Cli.Console, origin);
        return Task.FromResult(ExitCodes.Success);
    }

    private static string Kind(KeyNode node) => node.Kind switch
    {
        KeyKind.Object => "section",
        KeyKind.Dictionary => "map",
        _ => ScalarConverter.Describe(node),
    };

    private static string Who(KeyNode node) => node.Trust switch
    {
        TrustClass.Preference => "any tier",
        TrustClass.Tighten => "any tier; project files may only make it stricter",
        TrustClass.Sensitive => "user, environment, flags and managed settings; never project files",
        TrustClass.UserOnly => "personal; not accepted from project files",
        TrustClass.ManagedOnly => "managed settings only",
        _ => "any tier",
    };

    private static string StrictText(KeyNode node) => node.Strict switch
    {
        StrictDirection.Min => "may only be lowered",
        StrictDirection.Max => "may only be raised",
        StrictDirection.Subset => "may only be narrowed",
        StrictDirection.Order => $"may only move toward {node.StrictOrder.FirstOrDefault()}",
        _ => "",
    };
}
