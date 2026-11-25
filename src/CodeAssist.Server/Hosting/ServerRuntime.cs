using CodeAssist.Server.Composition;
using CodeAssist.Sessions;
using CodeAssist.Mcp;
using CodeAssist.Models.Catalog;
using CodeAssist.Models.Chat;
using CodeAssist.Server.Http;
using CodeAssist.Server.Telemetry;
using CodeAssist.Configuration;
using CodeAssist.Persistence;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeAssist.Server.Hosting;

public static class ServerRuntime
{
    public static async Task RunAsync(CodeAssistSettings settings, SettingsLocations locations,
        IReadOnlyDictionary<string, string> environment, Action<string> listening,
        string? address = null, McpHostServices? mcp = null, CancellationToken cancellation = default,
        Action<WebApplicationBuilder>? configure = null)
    {
        var http = settings.Host.Listen.Http;
        if (string.IsNullOrWhiteSpace(http.Token) || http.Token.Length < 32 || http.Token.Length > 512)
            throw new ArgumentException("HTTP authentication requires a token between 32 and 512 characters.", nameof(settings));
        using var catalogHttp = new HttpClient();
        catalogHttp.Timeout = TimeSpan.FromSeconds(settings.Catalog.TimeoutSeconds);
        var catalog = new ModelCatalogService(new ModelCatalogClient(catalogHttp),
            Path.Combine(locations.StateDirectory, "catalog"), () => settings.Catalog.Url);
        var registry = ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.FindModel);
        using var sessions = AgentSessionComposition.CreateAgentSessions(settings, registry, environment, locations: locations);
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(locations.WorkingDirectory));
        var workspaceKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? workspace.ToUpperInvariant() : workspace)));
        using var conversations = settings.Host.RecoverConversations ? new FileConversationStore(Path.Combine(
            Path.GetFullPath(settings.Storage.Directory ?? locations.StateDirectory, workspace), "conversations", workspaceKey)) : null;
        if (conversations is not null) await sessions.EnablePersistenceAsync(conversations, cancellation);
        await using var connectors = ConnectorComposition.Create(settings, locations.WorkingDirectory, environment, host: mcp);
        settings.Roles.TryGetValue(settings.Agent.Role, out var role);
        await using var app = CodeAssistHttpServer.Build(new()
        {
            Token = http.Token,
            Address = address ?? http.Address,
            DefaultModel = role?.Model ?? settings.Agent.Model,
            AllowCatalogRefresh = settings.Catalog.Refresh != CatalogRefresh.Never,
            AllowedOrigins = http.AllowedOrigins,
        }, sessions, new CatalogApi(catalog), builder =>
        {
            configure?.Invoke(builder);
            builder.Services.AddSingleton(new TelemetryExportConfiguration(settings.Telemetry, environment));
            builder.Services.AddSingleton(new PromptCommandCatalog(settings, SkillComposition.CreateTextCatalog(settings, locations),
                PluginComposition.CreateCatalog(settings, locations.StateDirectory, locations.WorkingDirectory)));
            builder.AddServiceDefaults();
        }, plugins: PluginComposition.CreateCatalog(settings, locations.StateDirectory, locations.WorkingDirectory),
            skills: SkillComposition.CreateTextCatalog(settings, locations),
            connectors: new(connectors, id => ToolComposition.CreateContext(settings, locations.WorkingDirectory, id, "connector-control"), connectors));
        app.MapManagement(settings, locations, environment, mcp);
        await app.StartAsync(cancellation);
        foreach (var endpoint in app.Urls) listening(endpoint);
        await app.WaitForShutdownAsync(cancellation);
    }
}