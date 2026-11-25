using System.Runtime.InteropServices;
using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Diagnostics;

public enum CheckLevel { Ok, Warn, Fail }

public sealed record DoctorCheck(string Name, CheckLevel Level, string Detail, string? Hint = null);

/// <summary>Checks the installation: settings validity, provider credentials and connector commands. Never prints secret values.</summary>
public sealed class DoctorCommand(CliContext cli) : CliCommand<OutputSettings>(cli)
{
    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, OutputSettings options, CancellationToken cancellation)
    {
        var checks = Collect();

        if (Cli.WantsJson(options.Json))
            Cli.WriteJson(checks.Select(c => new { c.Name, Level = c.Level.ToString().ToLowerInvariant(), c.Detail, c.Hint }).ToArray());
        else
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumn("").AddColumn("Check").AddColumn("Result");
            foreach (var check in checks)
            {
                var mark = check.Level switch { CheckLevel.Ok => "[green]ok[/]", CheckLevel.Warn => "[yellow]warn[/]", _ => "[red]fail[/]" };
                var detail = Markup.Escape(check.Detail) + (check.Hint is null ? "" : $"\n[grey]{Markup.Escape(check.Hint)}[/]");
                table.AddRow(mark, Markup.Escape(check.Name), detail);
            }
            Cli.Console.Write(table);
        }
        return Task.FromResult(checks.Any(c => c.Level == CheckLevel.Fail) ? ExitCodes.Failure : ExitCodes.Success);
    }

    private List<DoctorCheck> Collect()
    {
        var snapshot = Cli.Snapshot;
        var settings = snapshot.Settings;
        var environment = Cli.Environment.GetAll();
        var checks = new List<DoctorCheck>
        {
            new("Runtime", CheckLevel.Ok, $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}"),
        };

        var errors = snapshot.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = snapshot.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        checks.Add(errors > 0
            ? new DoctorCheck("Settings", CheckLevel.Fail, $"{errors} error(s), {warnings} warning(s)", "Run: code-assist config validate")
            : warnings > 0
                ? new DoctorCheck("Settings", CheckLevel.Warn, $"valid with {warnings} warning(s)", "Run: code-assist config validate")
                : new DoctorCheck("Settings", CheckLevel.Ok, $"valid (snapshot {snapshot.Id})"));

        var loaded = snapshot.Files.Where(f => f.Exists).Select(f => $"{SettingsJson.CamelCase(f.Tier.ToString())}").ToArray();
        checks.Add(new DoctorCheck("Settings files", CheckLevel.Ok, loaded.Length == 0 ? "none (built-in defaults)" : string.Join(", ", loaded)));

        if (!ModelRef.TryParse(settings.Agent.Model, out var model))
            checks.Add(new DoctorCheck("Default model", CheckLevel.Fail, $"'{settings.Agent.Model}' is not a model reference", "Run: code-assist config set agent.model <provider>/<model>"));
        else if (!settings.Providers.TryGetValue(model.Provider, out var provider))
            checks.Add(new DoctorCheck("Default model", CheckLevel.Fail, $"{model} uses provider '{model.Provider}', which is not defined", "Run: code-assist providers add " + model.Provider));
        else
        {
            var status = Cli.InspectProvider(model.Provider, provider);
            checks.Add(status.Usable
                ? new DoctorCheck("Default model", CheckLevel.Ok, $"{model} via {status.Endpoint}")
                : new DoctorCheck("Default model", CheckLevel.Fail, $"{model}: {status.Problem}", $"Set {ProviderInspector.SettingsKeyVariable(model.Provider)} or run: code-assist providers show {model.Provider}"));
        }

        foreach (var (id, provider) in settings.Providers.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ModelRef.TryParse(settings.Agent.Model, out var used) && string.Equals(used.Provider, id, StringComparison.OrdinalIgnoreCase)) continue;
            var status = Cli.InspectProvider(id, provider);
            if (!provider.Enabled) continue;
            checks.Add(status.Usable
                ? new DoctorCheck($"Provider {id}", CheckLevel.Ok, ProvidersCredentialText(status))
                : new DoctorCheck($"Provider {id}", CheckLevel.Warn, status.Problem ?? "not ready"));
        }

        foreach (var (name, server) in settings.Connectors.Servers.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!server.Enabled) continue;
            if (server.Transport == ConnectorTransport.Http)
            {
                checks.Add(new DoctorCheck($"Connector {name}", CheckLevel.Ok, server.Url ?? ""));
                continue;
            }
            var found = ExecutableLocator.Find(server.Command ?? "", environment, snapshot.Locations.WorkingDirectory);
            checks.Add(found is not null
                ? new DoctorCheck($"Connector {name}", CheckLevel.Ok, found)
                : new DoctorCheck($"Connector {name}", CheckLevel.Warn, $"command '{server.Command}' was not found on PATH"));
        }

        if (File.Exists(snapshot.Locations.ManagedFile))
            checks.Add(new DoctorCheck("Managed settings", CheckLevel.Ok, $"{snapshot.Locks.Patterns.Count} locked key pattern(s)"));
        return checks;
    }

    private static string ProvidersCredentialText(ProviderStatus status) => status.Source switch
    {
        CredentialSource.NotRequired => "no key required",
        CredentialSource.EntraIdentity => "Microsoft Entra sign-in",
        CredentialSource.CloudIdentity => status.SourceDetail ?? "cloud identity",
        CredentialSource.Vault => $"key from vault ({status.SourceDetail})",
        _ => $"key from {status.SourceDetail}",
    };
}
