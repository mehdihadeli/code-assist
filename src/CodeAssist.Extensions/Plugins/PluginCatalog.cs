using CodeAssist.Extensions.Skills;
using System.Text;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Extensions.Plugins;

public sealed record PluginEntry(string Name, string Plugin, string Release, string Digest, string Kind, string Description,
    IReadOnlyList<string> Tools, int DescriptionBytes, int ContentBytes);

public sealed record PluginRoleContent(string Prompt, IReadOnlyList<string> Tools);

public sealed class PluginCatalog(PluginStore store, string workspace, ISkillCatalog? fallback = null, Func<string, bool>? permitted = null) : ISkillCatalog, IPluginContent
{
    private IReadOnlyList<PluginPackage> Packages(string root) => PluginStore.Order(store.LoadEnabled(root).Where(package => permitted?.Invoke(package.Manifest.Id) != false).ToArray());

    public IReadOnlyList<PluginEntry> Entries => Describe(workspace);

    public IReadOnlyList<PluginCommandDto> Commands
    {
        get
        {
            var entries = Entries.Where(entry => entry.Kind is "skill" or "prompt" or "role" or "style").ToArray();
            if (entries.Length > 1024) throw new InvalidDataException("Too many plugin text contributions; reduce the active plugin set.");
            return entries.Select(entry => new PluginCommandDto(entry.Name, entry.Kind, entry.Description, entry.DescriptionBytes, entry.ContentBytes)).ToArray();
        }
    }

    public string ExpandPrompt(string name, string input) => Prompt(name, input);

    public IReadOnlyList<PluginEntry> Describe(string root) => Packages(root).SelectMany(package => package.Manifest.Contributions.Select(item =>
        new PluginEntry(package.Manifest.Id + "/" + item.Key, package.Manifest.Id, package.Manifest.Release, package.Digest, item.Kind, item.Description,
            item.Tools.ToArray(), Encoding.UTF8.GetByteCount(package.Manifest.Id + "/" + item.Key + " " + item.Description), package.FileBytes(item.Entry)))).ToArray();

    public IReadOnlyList<SkillSummary> Index => (fallback?.Index ?? []).Concat(Entries.Where(entry => entry.Kind == "skill")
        .Select(entry => new SkillSummary(entry.Name, entry.Description) { Origin = entry.Plugin, OriginKind = SkillOriginKind.Plugin })).OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<SkillSummary> Describe(ToolContext context, SkillCaller caller = SkillCaller.Agent) => (fallback?.Describe(context, caller) ?? [])
        .Concat(Describe(context.Workspace).Where(entry => entry.Kind == "skill").Select(entry => new SkillSummary(entry.Name, entry.Description) { Origin = entry.Plugin, OriginKind = SkillOriginKind.Plugin }))
        .OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();

    public async Task<SkillContent?> InvokeAsync(string id, SkillInvocation invocation, ToolContext context, CancellationToken cancellationToken)
    {
        var found = Find(id, context.Workspace);
        if (found is null) return fallback is null ? null : await fallback.InvokeAsync(id, invocation, context, cancellationToken);
        var content = await LoadAsync(id, context, cancellationToken);
        return content is null ? null : SkillRendering.Expand(content, invocation, context);
    }

    public async Task<SkillContent?> LoadAsync(string id, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = Find(id, context.Workspace);
        if (found is null) return fallback is null ? null : await fallback.LoadAsync(id, context, cancellationToken);
        var (package, contribution) = found.Value;
        if (contribution.Kind != "skill") return null;
        var text = Bounded(package, contribution.Entry, context.Limits.MaxFileBytes);
        store.RecordUse(package.Manifest.Id, contribution.Key, package.Digest);
        return new(id, text, package.Manifest.Assets.ToArray()) { Summary = new(id, contribution.Description) { Origin = package.Manifest.Id, OriginKind = SkillOriginKind.Plugin }, Digest = package.Digest };
    }

    public async Task<string?> ReadFileAsync(string id, string path, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = Find(id, context.Workspace);
        if (found is null) return fallback is null ? null : await fallback.ReadFileAsync(id, path, context, cancellationToken);
        var (package, contribution) = found.Value;
        if (contribution.Kind != "skill" || !package.Manifest.Assets.Contains(path, StringComparer.Ordinal)) return null;
        return Bounded(package, path, context.Limits.MaxFileBytes);
    }

    public string Prompt(string name, string input, string? root = null)
    {
        var (package, contribution) = Require(name, root ?? workspace);
        if (contribution.Kind is not ("skill" or "prompt" or "role" or "style")) throw new InvalidOperationException("Contribution is not text guidance.");
        var content = package.Text(contribution.Entry);
        var prompt = content + (string.IsNullOrWhiteSpace(input) ? "" : "\n\nUser input (untrusted task data):\n" + input);
        if (prompt.Length > 65_536) throw new InvalidDataException("Expanded plugin guidance exceeds the conversation input limit.");
        store.RecordUse(package.Manifest.Id, contribution.Key, package.Digest);
        return prompt;
    }

    public PluginService Service(string name, string? root = null)
    {
        var (package, contribution) = Require(name, root ?? workspace);
        if (contribution.Kind is not ("mcp" or "language")) throw new InvalidOperationException("Contribution is not a service.");
        return PluginDefinitions.Read<PluginService>(package.Text(contribution.Entry));
    }

    public PluginRoleContent Role(string name, string? root = null)
    {
        var (package, contribution) = Require(name, root ?? workspace);
        if (contribution.Kind != "role") throw new InvalidOperationException("Contribution is not a role.");
        var prompt = package.Text(contribution.Entry);
        if (prompt.Length > 64_000) throw new InvalidDataException("Plugin role instructions exceed their context limit.");
        return new(prompt, contribution.Tools.ToArray());
    }

    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(string name, IToolDispatcher dispatcher, ToolContext context, CancellationToken cancellationToken)
    {
        var (package, contribution) = Require(name, context.Workspace);
        if (contribution.Kind is not ("workflow" or "hook")) throw new InvalidOperationException("Contribution is not a tool sequence.");
        var sequence = PluginDefinitions.Read<PluginSequence>(package.Text(contribution.Entry));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(sequence.TimeoutSeconds));
        var results = new List<ToolResult>();
        foreach (var step in sequence.Steps)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!contribution.Tools.Contains(step.Tool, StringComparer.Ordinal) || dispatcher.Describe(step.Tool, context) is null)
                throw new UnauthorizedAccessException("Plugin requested a tool not available to this session.");
            var result = await dispatcher.InvokeAsync(step.Tool, step.Input.GetRawText(), context, deadline.Token);
            results.Add(result);
            if (result.IsError) break;
        }
        store.RecordUse(package.Manifest.Id, contribution.Key, package.Digest);
        return results;
    }

    public IReadOnlyList<string> Hooks(string eventName, string root) => Packages(root).SelectMany(package => package.Manifest.Contributions
        .Where(item => item.Kind == "hook" && PluginDefinitions.Read<PluginSequence>(package.Text(item.Entry)).Event == eventName)
        .Select(item => package.Manifest.Id + "/" + item.Key)).ToArray();

    private (PluginPackage Package, PluginContribution Contribution)? Find(string name, string root)
    {
        foreach (var package in Packages(root))
            foreach (var contribution in package.Manifest.Contributions)
                if (package.Manifest.Id + "/" + contribution.Key == name) return (package, contribution);
        return null;
    }

    private (PluginPackage Package, PluginContribution Contribution) Require(string name, string root) => Find(name, root)
        ?? throw new KeyNotFoundException("Plugin contribution is not enabled or is restricted by policy.");

    private static string Bounded(PluginPackage package, string entry, int limit)
    {
        if (package.FileBytes(entry) > Math.Min(limit, PluginFiles.MaxFileBytes)) throw new IOException("Plugin content exceeds the call's file budget.");
        var text = package.Text(entry);
        if (text.Contains('\0')) throw new InvalidDataException("Plugin resource is not text.");
        return text;
    }
}