using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersListCommand(CliContext cli) : CliCommand<OutputSettings>(cli)
{
    protected override Task<int> RunAsync(CommandContext context, OutputSettings options, CancellationToken cancellation)
    {
        var settings = Cli.Snapshot.Settings;
        var defaultProvider = ModelRef.TryParse(settings.Agent.Model, out var model) ? model.Provider : ModelRef.DefaultProvider;

        var statuses = settings.Providers
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => (Status: Cli.InspectProvider(p.Key, p.Value), Models: p.Value.Models.Count))
            .ToArray();

        if (Cli.WantsJson(options.Json))
        {
            Cli.WriteJson(statuses.Select(s => new
            {
                s.Status.Id,
                IsDefault = string.Equals(s.Status.Id, defaultProvider, StringComparison.OrdinalIgnoreCase),
                Protocol = s.Status.Protocol.ToString(),
                s.Status.Endpoint,
                s.Status.Enabled,
                Credential = s.Status.Source.ToString(),
                CredentialDetail = s.Status.SourceDetail,
                s.Status.Usable,
                s.Status.Problem,
                s.Models,
            }).ToArray());
            return Task.FromResult(ExitCodes.Success);
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Provider").AddColumn("Protocol").AddColumn("Endpoint").AddColumn("Credential").AddColumn("Models").AddColumn("Status");
        foreach (var (status, models) in statuses)
        {
            var isDefault = string.Equals(status.Id, defaultProvider, StringComparison.OrdinalIgnoreCase);
            table.AddRow(
                Markup.Escape(status.Id) + (isDefault ? " [green](default)[/]" : ""),
                status.Protocol.ToString().ToLowerInvariant(),
                Markup.Escape(status.Endpoint),
                Markup.Escape(CredentialText(status)),
                models.ToString(),
                status.Usable ? "[green]ready[/]" : $"[yellow]{Markup.Escape(status.Problem ?? "unavailable")}[/]");
        }
        Cli.Console.Write(table);
        Cli.Console.MarkupLine("[grey]Add one with [bold]code-assist providers add <id> --preset <preset>[/]; see [bold]providers presets[/].[/]");
        return Task.FromResult(ExitCodes.Success);
    }

    internal static string CredentialText(ProviderStatus status) => status.Source switch
    {
        CredentialSource.NotRequired => "not required",
        CredentialSource.EntraIdentity => "Microsoft Entra sign-in",
        CredentialSource.CloudIdentity => status.SourceDetail ?? "cloud identity",
        CredentialSource.Missing => "missing",
        CredentialSource.Vault => $"vault: {status.SourceDetail}",
        _ => $"env: {status.SourceDetail}",
    };
}
