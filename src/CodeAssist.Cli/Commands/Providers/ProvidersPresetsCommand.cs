using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersPresetsSettings : OutputSettings
{
    [CommandOption("--catalog"), Description("List providers discovered from the cached models.dev feed instead of built-in presets.")]
    public bool Catalog { get; init; }
}

public sealed class ProvidersPresetsCommand(CliContext cli) : CliCommand<ProvidersPresetsSettings>(cli)
{
    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, ProvidersPresetsSettings options, CancellationToken cancellation)
    {
        if (options.Catalog)
        {
            if (Cli.WantsJson(options.Json)) Cli.WriteJson(Cli.CatalogProviders);
            else
            {
                var catalogTable = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Provider").AddColumn("Name").AddColumn("Endpoint").AddColumn("Key variables");
                foreach (var provider in Cli.CatalogProviders)
                    catalogTable.AddRow(Markup.Escape(provider.Id), Markup.Escape(provider.Name), Markup.Escape(provider.Api ?? ""),
                        Markup.Escape(string.Join(", ", provider.Env)));
                Cli.Console.Write(catalogTable);
            }
            return Task.FromResult(ExitCodes.Success);
        }

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(ProviderPresets.All.Select(p => new
            {
                p.Id,
                p.DisplayName,
                Protocol = p.Protocol.ToString().ToLowerInvariant(),
                p.Endpoint,
                KeyVariables = p.KeyVariables,
                Api = p.Api.ToString().ToLowerInvariant(),
                p.Doc,
                p.NeedsEndpointEdit,
                p.Note,
            }).ToArray());
            return Task.FromResult(ExitCodes.Success);
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Preset").AddColumn("Protocol").AddColumn("Endpoint").AddColumn("Key variables").AddColumn("Notes");
        foreach (var p in ProviderPresets.All)
            table.AddRow(Markup.Escape(p.Id), p.Protocol.ToString().ToLowerInvariant(), Markup.Escape(p.Endpoint),
                Markup.Escape(p.KeyVariables.Count == 0 ? "none" : string.Join(", ", p.KeyVariables)), Markup.Escape(p.Note));
        Cli.Console.Write(table);
        return Task.FromResult(ExitCodes.Success);
    }
}
