using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersShowCommand(CliContext cli) : CliCommand<ProvidersShowCommand.Options>(cli)
{
    public sealed class Options : OutputSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Provider id.")]
        public string Id { get; init; } = "";
    }

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var settings = Cli.Snapshot.Settings;
        var key = settings.Providers.Keys.FirstOrDefault(k => string.Equals(k, options.Id, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            Cli.Console.MarkupLine($"[red]error[/] Provider '{Markup.Escape(options.Id)}' is not defined. Known: {Markup.Escape(string.Join(", ", settings.Providers.Keys))}.");
            return Task.FromResult(ExitCodes.Failure);
        }

        var provider = settings.Providers[key];
        var status = Cli.InspectProvider(key, provider);
        var redacted = Cli.Snapshot.Redacted()["providers"]![key]!;

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = key,
                ["usable"] = status.Usable,
                ["problem"] = status.Problem,
                ["credential"] = status.Source.ToString(),
                ["credentialDetail"] = status.SourceDetail,
                ["settings"] = redacted.DeepClone(),
            });
            return Task.FromResult(ExitCodes.Success);
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[bold]Protocol[/]", provider.Protocol.ToString().ToLowerInvariant());
        grid.AddRow("[bold]Endpoint[/]", Markup.Escape(status.Endpoint));
        grid.AddRow("[bold]Credential[/]", Markup.Escape(ProvidersListCommand.CredentialText(status)));
        grid.AddRow("[bold]Enabled[/]", provider.Enabled ? "yes" : "no");
        grid.AddRow("[bold]Timeout[/]", $"{provider.TimeoutSeconds}s, max {provider.MaxConcurrentRequests} concurrent");
        if (provider.Options.Count > 0)
            grid.AddRow("[bold]Options[/]", Markup.Escape(string.Join(", ", provider.Options.Select(o => $"{o.Key}={o.Value}"))));
        grid.AddRow("[bold]Status[/]", status.Usable ? "[green]ready[/]" : $"[yellow]{Markup.Escape(status.Problem ?? "unavailable")}[/]");
        Cli.Console.Write(new Panel(grid).Header(Markup.Escape(key)).Border(BoxBorder.Rounded));

        if (provider.Models.Count > 0)
        {
            var models = new Table().Border(TableBorder.Rounded)
                .AddColumn("Model").AddColumn("Name").AddColumn("Context").AddColumn("Tools").AddColumn("Reasoning").AddColumn("Enabled");
            foreach (var (id, model) in provider.Models.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
                models.AddRow(Markup.Escape(id), Markup.Escape(model.Name ?? ""), model.ContextWindow?.ToString() ?? "-",
                    model.Tools ? "yes" : "no", model.Reasoning ? "yes" : "no", model.Enabled ? "yes" : "no");
            Cli.Console.Write(models);
        }
        else Cli.Console.MarkupLine("[grey]No models are listed for this provider; any model id the endpoint accepts can be used.[/]");

        if (!status.Usable && status.Source == CredentialSource.Missing)
            Cli.Console.MarkupLine($"[grey]Provide a key with the environment variable {Markup.Escape(ProviderInspector.SettingsKeyVariable(key))}.[/]");
        return Task.FromResult(ExitCodes.Success);
    }
}
