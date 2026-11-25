using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.Presentation;
using CodeAssist.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersAddCommand(CliContext cli) : CliCommand<ProvidersAddCommand.Options>(cli)
{
    public sealed class Options : GlobalSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Provider id used in model references, for example work or local.")]
        public string Id { get; init; } = "";

        [CommandOption("--preset <PRESET>")]
        [Description("Start from a preset (see 'providers presets'). Defaults to a preset with the same id.")]
        public string? Preset { get; init; }

        [CommandOption("--endpoint <URL>")]
        [Description("Base URL. HTTPS is required except on loopback.")]
        public string? Endpoint { get; init; }

        [CommandOption("--protocol <PROTOCOL>")]
        [Description("auto, openai, anthropic, azure, gemini, vertex or bedrock.")]
        public string? Protocol { get; init; }

        [CommandOption("--default-model <NAME>")]
        [Description("Also make <ID>/<NAME> the default model.")]
        public string? DefaultModel { get; init; }

        [CommandOption("--option <KEY=VALUE>")]
        [Description("Adapter option, for example apiVersion=2024-10-21. Repeatable.")]
        public string[] ProviderOptions { get; init; } = [];

        [CommandOption("--scope <SCOPE>")]
        [Description("user (default). Providers cannot be added from a project file.")]
        public EditScope Scope { get; init; } = EditScope.User;

        [CommandOption("--force")]
        [Description("Replace an existing provider with the same id.")]
        public bool Force { get; init; }
    }

    protected override bool AllowInvalidSettings => true;

    protected override Task<int> RunAsync(CommandContext context, Options options, CancellationToken cancellation)
    {
        var id = options.Id;
        var preset = options.Preset is { } name ? ProviderPresets.Find(name) : ProviderPresets.Find(id);
        if (options.Preset is not null && preset is null)
            throw new UsageException($"Unknown preset '{options.Preset}'. Run 'code-assist providers presets' to list them.");
        var catalog = preset is null ? Cli.CatalogProviders.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

        var snapshot = Cli.Snapshot;
        var defined = snapshot.Settings.Providers.Keys.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (defined && !options.Force)
            throw new UsageException($"Provider '{id}' is already defined. Use --force to replace it or choose another id.");

        var protocolNames = SettingsJson.EnumNames(typeof(ProviderProtocol));
        var protocol = options.Protocol ?? (preset is null ? protocolNames[(int)ProviderProtocol.Auto] : protocolNames[(int)preset.Protocol]);
        var endpoint = options.Endpoint ?? (preset is { NeedsEndpointEdit: false } ? preset.Endpoint : null);
        if (endpoint is null && catalog is null)
            throw new UsageException(preset is null ? "--endpoint is required when no preset applies." : $"The '{preset.Id}' preset needs your own endpoint: pass --endpoint <URL>. {preset.Note}");

        var items = new List<(string, string)>
        {
            (SettingsPath.Format(["providers", id, "protocol"]), protocol),
        };
        if (endpoint is not null) items.Add((SettingsPath.Format(["providers", id, "endpoint"]), endpoint));
        if (catalog is not null)
        {
            items.Add((SettingsPath.Format(["providers", id, "displayName"]), catalog.Name));
            if (catalog.Env.FirstOrDefault() is { } variable)
                items.Add((SettingsPath.Format(["providers", id, "apiKey"]), "{env:" + variable + "}"));
        }

        if (preset is { Api: not ProviderApi.Auto })
            items.Add((SettingsPath.Format(["providers", id, "api"]), SettingsJson.EnumNames(typeof(ProviderApi))[(int)preset.Api]));

        var adapterOptions = preset?.Options?.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var item in options.ProviderOptions)
        {
            var split = item.IndexOf('=');
            if (split <= 0) throw new UsageException($"--option expects KEY=VALUE, got '{item}'.");
            adapterOptions[item[..split]] = item[(split + 1)..];
        }
        items.AddRange(adapterOptions.Select(o => (SettingsPath.Format(["providers", id, "options", o.Key]), o.Value)));

        if (options.DefaultModel is not null) items.Add(("agent.model", $"{id}/{options.DefaultModel}"));

        if (defined) Cli.Editor.Unset(SettingsPath.Format(["providers", id]), options.Scope);
        var result = Cli.Editor.SetMany(items, options.Scope);
        var code = EditReport.Print(Cli.Console, result, $"Added provider {id}");
        if (code != ExitCodes.Success) return Task.FromResult(code);

        var added = Cli.Snapshot.Settings.Providers[Cli.Snapshot.Settings.Providers.Keys.First(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase))];
        var status = Cli.InspectProvider(id, added);
        Cli.Console.MarkupLine(status.Usable
            ? $"[green]Ready[/] [grey]({Markup.Escape(ProvidersListCommand.CredentialText(status))})[/]"
            : $"[yellow]{Markup.Escape(status.Problem ?? "Not ready")}[/]");
        Cli.Console.MarkupLine("[grey]Keys are never written to settings files; supply them through the environment.[/]");
        return Task.FromResult(ExitCodes.Success);
    }
}
