using System.ComponentModel;
using System.Globalization;
using CodeAssist.Cli.Hosting;
using CodeAssist.Contracts;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersModelsSettings : OutputSettings
{
    [CommandArgument(0, "[provider]"), Description("Only models of this provider id.")]
    public string? Provider { get; init; }

    [CommandOption("--refresh"), Description("Fetch the latest catalog first. Not allowed when catalog.refresh is 'never'.")]
    public bool Refresh { get; init; }
}

public sealed class ProvidersModelsCommand(CliContext cli) : CliCommand<ProvidersModelsSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, ProvidersModelsSettings options, CancellationToken cancellation)
    {
        var catalogSettings = Cli.Snapshot.Settings.Catalog;
        var client = Cli.Api;

        if (options.Refresh)
        {
            if (catalogSettings.Refresh == CatalogRefresh.Never)
            {
                Cli.Console.MarkupLine("[red]error[/] catalog.refresh is 'never'; the catalog is cache-only.");
                return ExitCodes.UsageOrConfig;
            }

            var refresh = await client.RefreshCatalogAsync(cancellation);
            if (refresh.Error is not null)
                Cli.Console.MarkupLine($"[yellow]warning[/] refresh failed, showing cached catalog: {Markup.Escape(refresh.Error)}");
        }

        var models = client.ListModels(options.Provider);
        if (models.Count == 0)
        {
            Cli.Console.MarkupLine("[grey]No catalog models. Run with [bold]--refresh[/] to fetch the feed.[/]");
            return ExitCodes.Success;
        }

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(models);
            return ExitCodes.Success;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Provider").AddColumn("Model").AddColumn("Context").AddColumn("Max output")
            .AddColumn("Tools").AddColumn("Reasoning").AddColumn("Input $/M").AddColumn("Output $/M").AddColumn("Status");
        foreach (var m in models)
            table.AddRow(
                Markup.Escape(m.ProviderId),
                Markup.Escape(m.ModelId),
                Count(m.ContextWindow),
                Count(m.MaxOutputTokens),
                m.ToolCallSupport is { } tools ? tools ? "yes" : "no" : "-",
                m.Reasoning ? "yes" : "no",
                Price(m.Price?.Input),
                Price(m.Price?.Output),
                Markup.Escape(m.Status ?? "active"));
        Cli.Console.Write(table);
        return ExitCodes.Success;
    }

    private static string Count(int? value) => value is null ? "-" : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Price(decimal? value) => value is null ? "-" : value.Value.ToString("0.####", CultureInfo.InvariantCulture);
}
