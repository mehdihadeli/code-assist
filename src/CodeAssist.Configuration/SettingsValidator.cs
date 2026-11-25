using System.Text.RegularExpressions;

namespace CodeAssist.Configuration;

/// <summary>Cross-key checks that the per-key catalog cannot express.</summary>
public static partial class SettingsValidator
{
    public static readonly IReadOnlyList<string> Capabilities = ["fs.read", "fs.write", "fs.delete", "proc.exec", "net.fetch", "mcp.call", "delegate"];
    public static readonly IReadOnlyList<string> PolicyActions = ["provider.use", "tool.use", "connector.use", "skill.use", "plugin.use"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    public static void Validate(CodeAssistSettings settings, DiagnosticBag diags)
    {
        ValidatePlugins(settings, diags);
        foreach (var (id, provider) in settings.Providers)
        {
            var at = $"providers.{id}";
            CheckId(id, "provider", at, diags);
            if (!string.IsNullOrWhiteSpace(provider.Endpoint)) CheckEndpoint(provider.Endpoint, $"{at}.endpoint", diags);
            if (!string.IsNullOrWhiteSpace(provider.Proxy)) CheckEndpoint(provider.Proxy, $"{at}.proxy", diags, allowPlainHttp: true);
            foreach (var header in provider.Headers.Keys.Where(h => h.Contains(' ') || h.Contains(':')))
                diags.Error("invalid_header", $"Header name '{header}' is not valid.", $"{at}.headers");
            if (provider.Models.Values.Any(m => m.ContextWindow is { } w && m.MaxOutputTokens is { } o && o > w))
                diags.Warn("model_limits", "A model's maxOutputTokens exceeds its contextWindow.", $"{at}.models");
            if (provider.Canonical is { } canonical && !settings.Providers.ContainsKey(canonical) && ProviderPresets.Find(canonical) is null)
                diags.Warn("unknown_canonical", $"'{at}.canonical' names '{canonical}', which is neither a configured provider nor a preset.", $"{at}.canonical");
            if (provider.Encoding != RequestEncoding.None && provider.Endpoint.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
                diags.Warn("encoding_unsupported", "api.openai.com rejects compressed request bodies; leave encoding as 'none'.", $"{at}.encoding");
            CheckJsonFields(provider.Body, $"{at}.body", diags);
            foreach (var (modelKey, model) in provider.Models)
            {
                CheckJsonFields(model.Body, $"{at}.models.{modelKey}.body", diags);
                if (model.ReasoningControls.BudgetMin > model.ReasoningControls.BudgetMax)
                    diags.Error("reasoning_budget", "reasoningControls.budgetMin must not exceed budgetMax.", $"{at}.models.{modelKey}.reasoningControls");
            }
            foreach (var (variantKey, variant) in provider.Variants)
                CheckJsonFields(variant.Body, $"{at}.variants.{variantKey}.body", diags);
        }

        CheckModelRef(settings.Agent.Model, "agent.model", settings, diags, required: true);
        if (settings.Catalog.Refresh != CatalogRefresh.Never) CheckEndpoint(settings.Catalog.Url, "catalog.url", diags);
        if (settings.Agent.SmallModel is { } small) CheckModelRef(small, "agent.smallModel", settings, diags);
        for (var i = 0; i < settings.Agent.FallbackModels.Count; i++)
            CheckModelRef(settings.Agent.FallbackModels[i], $"agent.fallbackModels[{i}]", settings, diags);

        if (settings.Agent.Retry.BaseDelayMs > settings.Agent.Retry.MaxDelayMs)
            diags.Error("retry_delays", "agent.retry.baseDelayMs must not exceed maxDelayMs.", "agent.retry");

        if (!settings.Roles.ContainsKey(settings.Agent.Role))
            diags.Error("unknown_role", $"agent.role '{settings.Agent.Role}' is not defined under roles.", "agent.role");
        foreach (var (name, role) in settings.Roles)
        {
            CheckId(name, "role", $"roles.{name}", diags);
            if (role.Model is { } model) CheckModelRef(model, $"roles.{name}.model", settings, diags);
        }

        foreach (var (name, server) in settings.Connectors.Servers)
        {
            var at = $"connectors.servers.{name}";
            CheckId(name, "connector", at, diags);
            if (server.Transport == ConnectorTransport.Stdio)
            {
                if (string.IsNullOrWhiteSpace(server.Command))
                    diags.Error("connector_command", "A stdio connector needs 'command'.", $"{at}.command");
            }
            else if (string.IsNullOrWhiteSpace(server.Url))
                diags.Error("connector_url", "An http connector needs 'url'.", $"{at}.url");
            else CheckEndpoint(server.Url, $"{at}.url", diags);
        }

        for (var i = 0; i < settings.Rules.Count; i++)
            if (!Capabilities.Contains(settings.Rules[i].Capability, StringComparer.OrdinalIgnoreCase))
                diags.Error("unknown_capability", $"Unknown capability '{settings.Rules[i].Capability}'. Expected: {string.Join(", ", Capabilities)}.", $"rules[{i}].capability");

        for (var i = 0; i < settings.Policy.Rules.Count; i++)
            if (!PolicyActions.Contains(settings.Policy.Rules[i].Action, StringComparer.OrdinalIgnoreCase))
                diags.Error("unknown_policy_action", $"Unknown policy action '{settings.Policy.Rules[i].Action}'. Expected: {string.Join(", ", PolicyActions)}.", $"policy.rules[{i}].action");
    }

    private static void CheckJsonFields(SettingsMap<string> fields, string path, DiagnosticBag diags)
    {
        foreach (var (name, text) in fields)
        {
            try { System.Text.Json.Nodes.JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException)
            {
                diags.Error("invalid_json", $"'{name}' is not valid JSON text.", $"{path}.{name}");
            }
        }
    }

    private static void CheckId(string id, string kind, string path, DiagnosticBag diags)
    {
        if (!IdPattern().IsMatch(id))
            diags.Error("invalid_id", $"The {kind} id '{id}' must start with a letter or digit and use only letters, digits, '.', '_' or '-'.", path);
    }

    private static void CheckEndpoint(string? value, string path, DiagnosticBag diags, bool allowPlainHttp = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            diags.Error("invalid_url", $"'{value}' is not an absolute http(s) URL.", path);
            return;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
            diags.Error("url_credentials", "URLs must not embed credentials.", path);
        if (uri.Scheme == "http" && !allowPlainHttp && !IsLoopback(uri))
            diags.Error("insecure_url", $"'{value}' uses plain HTTP; HTTPS is required except on loopback.", path);
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static void CheckModelRef(string? text, string path, CodeAssistSettings settings, DiagnosticBag diags, bool required = false)
    {
        if (!ModelRef.TryParse(text, out var reference))
        {
            if (required || !string.IsNullOrWhiteSpace(text))
                diags.Error("invalid_model_ref", $"'{text}' is not a model reference (<provider>/<model>[@variant]).", path);
            return;
        }
        if (!settings.Providers.TryGetValue(reference.Provider, out var provider))
        {
            diags.Warn("unknown_provider", $"'{path}' refers to provider '{reference.Provider}', which is not defined.", path);
            return;
        }
        if (reference.Variant is { } variant && !provider.Variants.ContainsKey(variant) &&
            !(provider.Models.TryGetValue(reference.Model, out var model) && model.Variants.ContainsKey(variant)))
            diags.Warn("unknown_variant", $"'{path}' uses variant '{variant}', which is not defined for {reference.Provider}.", path);
    }
}
