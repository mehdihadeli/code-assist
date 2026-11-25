using CodeAssist.Server.Composition;
using CodeAssist.Models.Chat;
using CodeAssist.Configuration;
using CodeAssist.Testing.Live;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.IntegrationTests.Chat;

internal sealed record LiveModel(string ProviderId, string ModelId, bool Tools);

internal sealed class LiveProviderConfiguration
{
    private readonly LiveTestConfiguration _configuration;
    private readonly ProviderRegistry _registry = ProviderRegistry.CreateDefault();
    public CodeAssistSettings Settings { get; }
    public IReadOnlyList<LiveModel> Models { get; }

    private LiveProviderConfiguration(LiveTestConfiguration configuration, CodeAssistSettings settings)
    {
        _configuration = configuration;
        Settings = settings;
        var models = new List<LiveModel>();
        ModelRef.TryParse(settings.Agent.Model, out var selected);
        foreach (var (providerId, provider) in settings.Providers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!provider.Enabled) continue;
            foreach (var (modelId, model) in provider.Models.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (model.Enabled && model.Type == ModelType.Chat && model.Output.Contains(Modality.Text)) models.Add(new(providerId, modelId, model.Tools));
            if (provider.Models.Count == 0 && selected is not null && string.Equals(selected.Provider, providerId, StringComparison.OrdinalIgnoreCase))
                models.Add(new(providerId, selected.Model, true));
        }
        Models = models;
    }

    public static LiveProviderConfiguration Load(LiveTestConfiguration configuration, string? workingDirectory = null)
    {
        var locations = SettingsLocations.Resolve(configuration.Environment, workingDirectory ?? configuration.RepositoryDirectory ?? Directory.GetCurrentDirectory());
        var snapshot = SettingsLoader.Load(new(locations, configuration.Environment, new Dictionary<string, string>()));
        if (!snapshot.IsValid) throw new InvalidOperationException("Invalid live provider settings: " + string.Join(", ", snapshot.Diagnostics.Select(diagnostic => $"{diagnostic.Code} ({diagnostic.Path})")));
        return new(configuration, snapshot.Settings);
    }

    public IChatClient CreateClient(string providerId, string modelId, HttpMessageHandler? handler = null)
    {
        if (!_configuration.Enabled) throw new InvalidOperationException("Live requests require ENABLED_LIVE_TESTS=1.");
        if (!Models.Any(model => model.ProviderId == providerId && model.ModelId == modelId)) throw new InvalidOperationException("Model is not enabled for live chat tests.");
        var provider = Settings.Providers[providerId];
        var connection = ProviderComposition.ToConnection(providerId, provider, _registry, _configuration.Environment);
        var resolved = _registry.Resolve(connection, provider.Models.GetValueOrDefault(modelId)?.ModelId ?? modelId);
        if (string.IsNullOrWhiteSpace(connection.ApiKey) && !resolved.Adapter.KeyOptional) throw new InvalidOperationException($"No credentials for provider '{providerId}'. Set Providers__{providerId}__ApiKey or the variable named by its apiKey reference.");
        return ProviderComposition.CreateChatClient(providerId, modelId, provider, _registry, _configuration.Environment, handler);
    }
}