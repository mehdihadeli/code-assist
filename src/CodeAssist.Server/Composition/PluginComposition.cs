using CodeAssist.Extensions.Skills;
using System.IO.Enumeration;
using CodeAssist.Tools;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Configuration;

namespace CodeAssist.Server.Composition;

public static class PluginComposition
{
    public static PluginStore CreateStore(string stateDirectory) => new(Path.Combine(stateDirectory, "plugins"));

    public static PluginStore CreateStore(CodeAssistSettings settings, string stateDirectory) => new(Path.Combine(stateDirectory, "plugins"),
        package => Restriction(settings, package), settings.Policy.Plugins.Required, source => SourceRestriction(settings, source));

    public static string? Restriction(CodeAssistSettings settings, PluginPackage package)
    {
        var policy = settings.Policy.Plugins;
        var id = package.Manifest.Id;
        if (!Permitted(settings, id))
            return "Plugin id is not admitted by settings or organization policy.";
        if (policy.SequenceDigests is not null && package.Manifest.Contributions.Any(item => item.Kind is "hook" or "workflow") &&
            !policy.SequenceDigests.Contains(package.Digest, StringComparer.Ordinal))
            return "Hook/workflow content has not been admitted by the administrator.";
        return null;
    }

    public static PluginPackage ReadPackage(string source) => Directory.Exists(source) ? PluginPackage.Open(source) : PluginPackage.FromArchive(source);

    public static PluginPackage Scaffold(string directory, string id) => PluginPackage.Scaffold(directory, id);

    public static Task<PluginRegistry> OpenRegistryAsync(string source, CancellationToken cancellationToken) => PluginRegistry.OpenAsync(source, cancellationToken);

    public static Task<PluginRegistry> OpenRegistryAsync(CodeAssistSettings settings, string source, string workspace, CancellationToken cancellationToken)
    {
        var resolved = ResolveRegistrySource(settings, source, workspace);
        if (SourceRestriction(settings, resolved) is { } reason) throw new InvalidOperationException(reason);
        return PluginRegistry.OpenAsync(resolved, cancellationToken);
    }

    private static string? SourceRestriction(CodeAssistSettings settings, string? source)
    {
        if (source is null) return settings.Policy.Plugins.LocalPackages ? null : "Direct or legacy plugin content is restricted; reinstall from an admitted registry.";
        var allowed = settings.Policy.Plugins.Sources;
        if (allowed is not null && !allowed.Any(item => (item.Contains("://", StringComparison.Ordinal) || Path.IsPathFullyQualified(item)) &&
            PluginRegistry.SourceKey(item) == PluginRegistry.SourceKey(source)))
            return "Registry source is restricted by organization policy. Ask an administrator to admit its exact source.";
        return null;
    }

    private static string ResolveRegistrySource(CodeAssistSettings settings, string source, string workspace)
    {
        if (settings.PluginLibraries.TryGetValue(source, out var library)) source = library.Source;
        return source.Contains("://", StringComparison.Ordinal) ? source : Path.GetFullPath(source, workspace);
    }

    public static void Publish(string source, string registry, string name, string lane = "general") => PluginRegistry.Publish(ReadPackage(source), registry, name, lane);

    public static PluginCatalog CreateCatalog(CodeAssistSettings settings, string stateDirectory, string workspace, ISkillCatalog? fallback = null) =>
        new(CreateStore(settings, stateDirectory), workspace, fallback, id => Permitted(settings, id));

    public static bool Permitted(CodeAssistSettings settings, string id) =>
        (settings.Policy.Plugins.Required.ContainsKey(id) || !settings.Plugins.Any(plugin => plugin.Package == id && !plugin.Enabled)) &&
        !settings.Policy.Plugins.Reject.Contains(id, StringComparer.Ordinal) &&
        (settings.Policy.Plugins.Ids is null || settings.Policy.Plugins.Ids.Contains(id, StringComparer.Ordinal)) &&
        !settings.Policy.Rules.Any(rule => rule.Action.Equals("plugin.use", StringComparison.OrdinalIgnoreCase) && rule.Effect != RuleDecision.Allow &&
            FileSystemName.MatchesSimpleExpression(rule.Resource, id, ignoreCase: true));

    public static RoleSettings? ResolveRole(CodeAssistSettings settings, PluginCatalog plugins, string name, string? workspace = null)
    {
        if (settings.Roles.TryGetValue(name, out var configured)) return configured;
        if (!(workspace is null ? plugins.Entries : plugins.Describe(workspace)).Any(entry => entry.Name == name && entry.Kind == "role")) return null;
        var role = plugins.Role(name, workspace);
        return new() { Delegatable = true, Prompt = role.Prompt, Tools = new() { Allow = role.Tools.ToList() } };
    }

    public static IReadOnlyDictionary<string, RoleSettings> Roles(CodeAssistSettings settings, PluginCatalog plugins, string? workspace = null) => (workspace is null ? plugins.Entries : plugins.Describe(workspace))
        .Where(entry => entry.Kind == "role" && !settings.Roles.ContainsKey(entry.Name))
        .ToDictionary(entry => entry.Name, entry => ResolveRole(settings, plugins, entry.Name, workspace)!, StringComparer.Ordinal);
}