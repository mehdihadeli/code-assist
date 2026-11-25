using System.Text.Json;
using CodeAssist.Models.Catalog;
using CodeAssist.Models.Chat;
using CodeAssist.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeAssist.Server.Composition;

public static class ProviderComposition
{
    public static IServiceCollection AddCodeAssistProviders(this IServiceCollection services)
    {
        services.AddCodeAssistCatalog();
        services.TryAddSingleton(provider =>
        {
            var catalog = provider.GetRequiredService<ModelCatalogService>();
            return ProviderRegistry.CreateDefault(catalog.FindProvider, catalog.FindModel);
        });
        return services;
    }

    public static IChatClient CreateChatClient(string id, string model, ProviderSettings settings,
        ProviderRegistry registry, IReadOnlyDictionary<string, string> environment, HttpMessageHandler? handler = null, string? variant = null,
        Func<string, bool>? admitHosted = null)
    {
        if (!settings.Enabled) throw new InvalidOperationException($"Provider '{id}' is disabled.");
        settings.Models.TryGetValue(model, out var modelSettings);
        if (modelSettings?.Enabled == false) throw new InvalidOperationException($"Model '{id}/{model}' is disabled.");
        if (modelSettings is { Type: not ModelType.Chat }) throw new NotSupportedException("Only chat models can create an IChatClient.");
        var connection = ToConnection(id, settings, registry, environment);
        var metadata = ConfiguredModelMetadata.Resolve(id, model, settings, (provider, name) => registry.FindModel(new ProviderConnection { Id = provider }, name));
        connection = connection with { CatalogModelId = model, ModelMetadata = metadata };
        if (modelSettings is not null)
        {
            connection = connection with
            {
                CatalogModelId = model,
                MaxOutputTokens = modelSettings.MaxOutputTokens ?? connection.MaxOutputTokens,
                Headers = Merge(connection.Headers, modelSettings.Headers),
                Body = Merge(connection.Body, modelSettings.Body),
                Options = Merge(connection.Options, modelSettings.Options),
            };
        }
        if (variant is not null)
        {
            if (!(modelSettings?.Variants.TryGetValue(variant, out var overlay) ?? false) && !settings.Variants.TryGetValue(variant, out overlay))
                throw new InvalidOperationException($"Model variant '{variant}' is not configured.");
            var body = Merge(connection.Body, overlay.Body);
            var wire = registry.Resolve(connection, model).Connection.Wire;
            if (overlay.Temperature is { } temperature) body["temperature"] = JsonSerializer.Serialize(temperature);
            if (overlay.TopP is { } topP) body["top_p"] = JsonSerializer.Serialize(topP);
            if ((overlay.Temperature is not null || overlay.TopP is not null) && wire is not (WireProtocol.OpenAiChat or WireProtocol.OpenAiResponses or WireProtocol.Anthropic))
                throw new NotSupportedException("Sampling controls for this protocol require explicit variant body fields.");
            if (overlay.Effort is { } effort)
            {
                if (wire == WireProtocol.OpenAiChat) body["reasoning_effort"] = JsonSerializer.Serialize(effort);
                else if (wire == WireProtocol.OpenAiResponses) body["reasoning"] = JsonSerializer.Serialize(new { effort });
                else throw new NotSupportedException("Effort controls for this protocol require explicit variant body fields.");
            }
            if (overlay.ThinkingBudget is { } budget)
            {
                if (wire != WireProtocol.Anthropic) throw new NotSupportedException("Thinking budgets for this protocol require explicit variant body fields.");
                body["thinking"] = JsonSerializer.Serialize(new { type = "enabled", budget_tokens = budget });
            }
            connection = connection with { Headers = Merge(connection.Headers, overlay.Headers), Options = Merge(connection.Options, overlay.Options), Body = body };
        }
        connection = HostedConnectorComposition.Apply(connection, settings.RemoteTools, environment, admitHosted);
        return registry.CreateChatClient(connection, modelSettings?.ModelId ?? model, handler);
    }

    /// <summary>
    /// Turns a provider's settings into a resolved connection. The key comes from <c>Providers__id__ApiKey</c>, then the
    /// provider's exact environment reference, then the adapter's conventional variables. No key is saved or logged.
    /// </summary>
    /// <exception cref="NotSupportedException">The settings ask for something Providers cannot do (zstd).</exception>
    public static ProviderConnection ToConnection(
        string id, ProviderSettings settings, ProviderRegistry registry, IReadOnlyDictionary<string, string> environment)
    {
        if (settings.Encoding == RequestEncoding.Zstd) throw new NotSupportedException("Request encoding 'zstd' is not supported; use 'gzip' or 'none'.");
        if (settings.Transport != ProviderTransport.Http) throw new NotSupportedException("WebSocket provider transport is not supported; use 'http'.");
        if (settings.Credential is not null) throw new NotSupportedException("Vault credentials are not supported yet; supply a key through the environment.");

        Uri? endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? null
            : Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) ? uri : throw new ArgumentException($"Provider '{id}' has an invalid endpoint.");
        if ((settings.Protocol == ProviderProtocol.Azure || settings.Protocol == ProviderProtocol.Auto && registry.Find(id)?.Wire == WireProtocol.AzureOpenAi)
            && settings.Options.ContainsKey("resourceName")) endpoint = null;

        return new ProviderConnection
        {
            Id = id,
            Canonical = settings.Canonical,
            Wire = WireFor(settings, endpoint),
            Endpoint = endpoint,
            ApiKey = ResolveKey(id, settings, registry, environment),
            MaxOutputTokens = settings.MaxOutputTokens,
            Headers = new Dictionary<string, string>(settings.Headers),
            Body = new Dictionary<string, string>(settings.Body),
            Options = ResolveOptions(id, settings, registry, environment),
            Limits = new TransportLimits(
                TimeSpan.FromSeconds(settings.TimeoutSeconds),
                TimeSpan.FromSeconds(settings.HeaderTimeoutSeconds),
                TimeSpan.FromSeconds(settings.ChunkTimeoutSeconds)),
            Encoding = settings.Encoding == RequestEncoding.Gzip ? BodyEncoding.Gzip : BodyEncoding.None,
            Proxy = settings.Proxy,
            MaxConcurrentRequests = settings.MaxConcurrentRequests,
        };
    }

    private static WireProtocol WireFor(ProviderSettings settings, Uri? endpoint) => settings.Protocol switch
    {
        ProviderProtocol.Anthropic => WireProtocol.Anthropic,
        ProviderProtocol.Azure => WireProtocol.AzureOpenAi,
        ProviderProtocol.Gemini => WireProtocol.Gemini,
        ProviderProtocol.Vertex => WireProtocol.Vertex,
        ProviderProtocol.Bedrock => WireProtocol.Bedrock,
        ProviderProtocol.Auto when settings.Api == ProviderApi.Auto => WireProtocol.Auto,
        _ => settings.Api switch
        {
            ProviderApi.Chat => WireProtocol.OpenAiChat,
            ProviderApi.Responses => WireProtocol.OpenAiResponses,
            _ => string.Equals(endpoint?.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase) ? WireProtocol.OpenAiResponses : WireProtocol.OpenAiChat,
        },
    };

    private static string? ResolveKey(string id, ProviderSettings settings, ProviderRegistry registry, IReadOnlyDictionary<string, string> environment)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return EnvironmentCredential.Resolve(settings.ApiKey, environment);
        if (environment.TryGetValue(ProviderInspector.SettingsKeyVariable(id), out var configured) && !string.IsNullOrWhiteSpace(configured))
            return EnvironmentCredential.Resolve(configured, environment);

        foreach (var name in registry.KeyVariables(id))
        {
            if (environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    private static Dictionary<string, string> ResolveOptions(string id, ProviderSettings settings, ProviderRegistry registry, IReadOnlyDictionary<string, string> environment)
    {
        var result = new Dictionary<string, string>(settings.Options, StringComparer.OrdinalIgnoreCase);
        var wire = settings.Protocol == ProviderProtocol.Auto ? registry.Find(id)?.Wire : WireFor(settings, null);
        if (wire == WireProtocol.Bedrock)
        {
            Set("region", "AWS_REGION", "AWS_DEFAULT_REGION");
            Set("profile", "AWS_PROFILE");
        }
        if (wire == WireProtocol.Vertex)
        {
            Set("project", "GOOGLE_VERTEX_PROJECT", "GOOGLE_CLOUD_PROJECT", "GCP_PROJECT", "GCLOUD_PROJECT");
            Set("location", "GOOGLE_VERTEX_LOCATION", "GOOGLE_CLOUD_LOCATION", "VERTEX_LOCATION");
        }
        return result;

        void Set(string option, params string[] names)
        {
            if (result.ContainsKey(option)) return;
            foreach (var name in names)
            {
                if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                result[option] = value;
                return;
            }
        }
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> provider, IReadOnlyDictionary<string, string> model)
    {
        var result = new Dictionary<string, string>(provider, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in model) result[name] = value;
        return result;
    }
}
