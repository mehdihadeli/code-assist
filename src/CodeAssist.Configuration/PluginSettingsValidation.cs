using System.Text.RegularExpressions;

namespace CodeAssist.Configuration;

public static partial class SettingsValidator
{
    private static void ValidatePlugins(CodeAssistSettings settings, DiagnosticBag diags)
    {
        var policy = settings.Policy.Plugins;
        static bool Digest(string value) => Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
        static bool PluginId(string value) => value.Length <= 128 && Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,31}(\\.[a-z][a-z0-9-]{0,31}){1,3}$", RegexOptions.CultureInvariant);
        foreach (var id in (policy.Ids ?? []).Concat(policy.Reject).Concat(policy.Required.Keys))
            if (!PluginId(id)) diags.Error("plugin_policy_id", "Plugin policy requires exact lowercase dotted ids.", "policy.plugins");
        foreach (var digest in (policy.SequenceDigests ?? []).Concat(policy.Required.Values))
            if (!Digest(digest)) diags.Error("plugin_policy_digest", "Reviewed plugin content requires a lowercase SHA-256 digest.", "policy.plugins");
        foreach (var id in policy.Required.Keys)
            if (policy.Reject.Contains(id, StringComparer.Ordinal) || policy.Ids is not null && !policy.Ids.Contains(id, StringComparer.Ordinal))
                diags.Error("plugin_policy_conflict", "A required plugin must be admitted by the id policy.", "policy.plugins.required");
        foreach (var source in policy.Sources ?? [])
        {
            var safe = source.Contains("://", StringComparison.Ordinal)
                ? Uri.TryCreate(source, UriKind.Absolute, out var address) && address.Scheme == "https" && address.UserInfo.Length == 0 && address.Fragment.Length == 0
                : Path.IsPathFullyQualified(source);
            if (!safe) diags.Error("plugin_policy_source", "Policy sources must be literal HTTPS URLs or absolute paths, never library aliases or workspace-relative paths.", "policy.plugins.sources");
        }
        if ((policy.Ids?.Count ?? 0) > 128 || policy.Reject.Count > 128 || policy.Required.Count > 128 || (policy.Sources?.Count ?? 0) > 64 || (policy.SequenceDigests?.Count ?? 0) > 128)
            diags.Error("plugin_policy_limit", "Plugin policy exceeds its 128 package/digest or 64 source limit.", "policy.plugins");
        if (settings.PluginLibraries.Count > 64) diags.Error("plugin_library_limit", "At most 64 named plugin libraries are supported.", "pluginLibraries");
    }
}