using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAssist.Cli.Hosting;
using CodeAssist.Client;
using CodeAssist.Contracts;
using CodeAssist.Configuration;
using Spectre.Console;

namespace CodeAssist.Cli.Commands;

/// <summary>Everything a command normally needs, injected as one object to keep command constructors short.</summary>
public sealed class CliContext(
    IAnsiConsole console,
    SettingsService settings,
    SettingsEditor editor,
    IEnvironmentVariables environment,
    Hosting.CommandLifetime lifetime) : IDisposable
{
    private IReadOnlyList<CatalogProviderDto>? _catalogProviders;
    private ManagedServer? _server;
    private CodeAssistClient? _externalClient;

    public IAnsiConsole Console { get; } = console;
    public SettingsService Settings { get; } = settings;
    public SettingsEditor Editor { get; } = editor;
    public IEnvironmentVariables Environment { get; } = environment;
    public CancellationToken Cancellation => lifetime.Token;

    public SettingsSnapshot Snapshot => Settings.Current;

    public CodeAssistClient Api => _externalClient ?? (_server ??= LaunchServerAsync(cancellation: Cancellation).GetAwaiter().GetResult()).Client;

    public async Task ConnectAsync(Uri? endpoint, CancellationToken cancellation)
    {
        if (endpoint is null)
        {
            _server ??= await LaunchServerAsync(cancellation: cancellation);
            return;
        }
        if (_server is not null || _externalClient is not null) throw new UsageException("A service connection is already active.");
        var token = Environment.GetAll().FirstOrDefault(item => item.Key.Equals("Host__Listen__Http__Token", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrEmpty(token)) throw new UsageException("Set Host__Listen__Http__Token in the environment to attach to a service.");
        CodeAssistClient client;
        try { client = new CodeAssistClient(endpoint, token); }
        catch (ArgumentException) { throw new UsageException("Use a loopback service origin and a valid environment token."); }
        try
        {
            await client.CheckAsync(cancellation);
            _externalClient = client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public IReadOnlyList<CatalogProviderDto> CatalogProviders => _catalogProviders ??=
        Api.ListProviders();

    public Task<ManagedServer> LaunchServerAsync(string address = "127.0.0.1:0", string? token = null, CancellationToken cancellation = default, string? workingDirectory = null)
    {
        var values = new Dictionary<string, string>(Environment.GetAll(), StringComparer.OrdinalIgnoreCase);
        values[SettingsLocations.HomeVariable] = Settings.Locations.Home;
        values[SettingsLocations.ManagedVariable] = Settings.Locations.ManagedDirectory;
        foreach (var flag in Settings.Flags.Values) values[flag.Key.Replace(".", "__", StringComparison.Ordinal)] = flag.Value;
        return ManagedServer.StartAsync(Path.Combine(AppContext.BaseDirectory, "service", "CodeAssist.Server.dll"),
            workingDirectory ?? Settings.Locations.WorkingDirectory, values, address, cancellation, token);
    }

    public async Task<int> ManageAsync(string domain, string operation, OutputSettings options, CancellationToken cancellation, bool alwaysJson = false)
    {
        var reply = await Api.PostAsync<ManagementReply>($"/v1/management/{domain}",
            new ManagementCommandRequest(operation, JsonSerializer.SerializeToElement(options, options.GetType(), JsonSerializerOptions.Web)), cancellation);
        if (reply.ExitCode != 0 && !reply.HasValue) throw new UsageException(reply.Summary);
        if (alwaysJson || WantsJson(options.Json)) WriteJson(reply.Value);
        else Console.WriteLine(reply.Summary);
        return reply.ExitCode;
    }

    public void Dispose()
    {
        _externalClient?.Dispose();
        _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public ProviderStatus InspectProvider(string id, ProviderSettings settings)
    {
        var entry = CatalogProviders.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));
        return ProviderInspector.Inspect(id, settings, Environment.GetAll(), entry?.Api, ProviderPresets.Find(id)?.KeyVariables ?? entry?.Env);
    }

    /// <summary>Machine-readable output goes straight to the output writer: no markup, no wrapping, no colors.</summary>
    public void WriteJson(JsonNode? value) =>
        Console.Profile.Out.Writer.WriteLine(value is null ? "null" : value.ToJsonString(SettingsJson.Indented));

    public void WriteJson<T>(T value) =>
        Console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(value, SettingsJson.Indented));

    public bool WantsJson(bool flag) => flag || Snapshot.Settings.Cli.Output != OutputFormat.Text;
}
