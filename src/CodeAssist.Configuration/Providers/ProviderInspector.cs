namespace CodeAssist.Configuration;

public enum CredentialSource
{
    /// <summary>Key supplied through the settings environment variable (Providers__id__ApiKey).</summary>
    SettingsEnvironment,
    /// <summary>A named credential in the vault.</summary>
    Vault,
    /// <summary>The provider's conventional variable, for example OPENAI_API_KEY.</summary>
    ConventionalEnvironment,
    /// <summary>Loopback endpoint; no key needed.</summary>
    NotRequired,
    /// <summary>Azure endpoint without a key: signs in with the developer's Microsoft Entra identity.</summary>
    EntraIdentity,
    /// <summary>A key is expected but none was found.</summary>
    Missing,
    CloudIdentity,
    ReferencedEnvironment,
}

public sealed record ProviderStatus(
    string Id,
    bool Enabled,
    ProviderProtocol Protocol,
    string Endpoint,
    CredentialSource Source,
    string? SourceDetail,
    bool Usable,
    string? Problem);

/// <summary>Describes where a provider's credential would come from without ever reading the secret into output.</summary>
public static class ProviderInspector
{
    public static string SettingsKeyVariable(string id) => $"Providers__{id}__ApiKey";

    public static ProviderStatus Inspect(string id, ProviderSettings provider, IReadOnlyDictionary<string, string> environment,
        string? defaultEndpoint = null, IReadOnlyList<string>? keyVariables = null)
    {
        var settingsVariable = SettingsKeyVariable(id);
        var preset = ProviderPresets.Find(id);
        var protocol = provider.Protocol == ProviderProtocol.Auto ? preset?.Protocol ?? ProviderProtocol.Auto : provider.Protocol;
        var endpoint = string.IsNullOrWhiteSpace(provider.Endpoint) ? defaultEndpoint ?? preset?.Endpoint ?? "" : provider.Endpoint;
        if (endpoint.Length == 0 && protocol == ProviderProtocol.Azure && provider.Options.TryGetValue("resourceName", out var resource))
            endpoint = $"https://{resource}.openai.azure.com/";
        var variables = (keyVariables ?? preset?.KeyVariables ?? []).Distinct().ToArray();
        var loopback = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && SettingsValidator.IsLoopback(uri);
        var (source, detail) = Resolve(provider, environment, settingsVariable);

        string? problem = null;
        if (!provider.Enabled) problem = "disabled";
        else if (source == CredentialSource.Missing)
            problem = $"no credential: set {detail ?? settingsVariable}" + (detail is null && variables.Length > 0 ? $" or {string.Join(" or ", variables)}" : "");
        else if (uri is null && protocol is not (ProviderProtocol.Vertex or ProviderProtocol.Bedrock)) problem = "invalid endpoint";

        return new ProviderStatus(id, provider.Enabled, protocol, endpoint, source, detail, problem is null, problem);

        (CredentialSource, string?) Resolve(ProviderSettings settings, IReadOnlyDictionary<string, string> env, string variable)
        {
            var configured = !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey : Has(env, variable) ? env[variable] : null;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var reference = EnvironmentCredential.TryGetVariable(configured, out var referenced);
                try
                {
                    EnvironmentCredential.Resolve(configured, env);
                    return (reference ? CredentialSource.ReferencedEnvironment : CredentialSource.SettingsEnvironment, reference ? referenced : variable);
                }
                catch (InvalidOperationException) { return (CredentialSource.Missing, reference ? referenced : variable); }
            }
            if (!string.IsNullOrWhiteSpace(settings.Credential)) return (CredentialSource.Vault, settings.Credential);
            if (variables.FirstOrDefault(v => Has(env, v)) is { } conventional)
                return (CredentialSource.ConventionalEnvironment, conventional);
            if (protocol == ProviderProtocol.Azure) return (CredentialSource.EntraIdentity, "Microsoft Entra");
            if (protocol == ProviderProtocol.Vertex) return (CredentialSource.CloudIdentity, "Google Application Default Credentials");
            if (protocol == ProviderProtocol.Bedrock) return (CredentialSource.CloudIdentity, "AWS credential chain");
            return loopback ? (CredentialSource.NotRequired, null) : (CredentialSource.Missing, null);
        }
    }

    private static bool Has(IReadOnlyDictionary<string, string> env, string name) =>
        env.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value);
}
