using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAssist.Extensions.Plugins;

public sealed class PluginStore(string directory, Func<PluginPackage, string?>? restriction = null, IReadOnlyDictionary<string, string>? requiredDigests = null,
    Func<string?, string?>? sourceRestriction = null)
{
    private readonly Dictionary<string, string> _required = requiredDigests?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<PluginStatus> List(string? workspace = null)
    {
        var state = ReadState();
        return state.Installed.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item =>
        {
            var package = ReadPackage(item);
            return new PluginStatus(item.Id, item.Release, item.Digest, Enabled(state, item, workspace), package.Manifest.Summary, package.Manifest.Contributions.Count, package.Bytes);
        }).ToArray();
    }

    public IReadOnlyList<PluginPackage> LoadEnabled(string? workspace = null)
    {
        var state = ReadState();
        foreach (var required in _required)
            if (!state.Installed.Any(item => item.Id == required.Key && item.Digest == required.Value))
                throw new InvalidOperationException($"Required plugin {required.Key} is missing or has a different digest. Install the administrator-reviewed release.");
        var packages = Bounded(state.Installed.Where(item => Enabled(state, item, workspace)).Select(ReadPackage));
        foreach (var package in packages) CheckPolicy(package, Find(state, package.Manifest.Id).Source);
        return Order(packages);
    }

    public IReadOnlyDictionary<string, PluginUsage> Usage() => ReadState().Usage;

    public PluginLoadReport Diagnose(string? workspace = null)
    {
        var issues = new List<PluginLoadIssue>();
        var entries = new List<PluginLoadEntry>();
        PluginState state;
        try { state = ReadState(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new(false, [], [new(null, "state-unreadable", "Plugin installation state cannot be validated.", "Preserve installed.json, repair its schema or restore a known-good backup. No state was rewritten.", true)]);
        }
        var available = new Dictionary<string, PluginPackage>(StringComparer.Ordinal);
        foreach (var required in _required)
            if (!state.Installed.Any(item => item.Id == required.Key && item.Digest == required.Value))
                issues.Add(new(required.Key, "required-content", "Required reviewed content is not installed.", "Install the exact release and content digest supplied by the administrator.", true));
        foreach (var install in state.Installed.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var selected = Enabled(state, install, workspace);
            var contributions = 0;
            try
            {
                var package = ReadPackage(install);
                contributions = package.Manifest.Contributions.Count;
                if (selected)
                {
                    try
                    {
                        CheckPolicy(package, install.Source);
                        available.Add(install.Id, package);
                    }
                    catch (InvalidOperationException)
                    {
                        issues.Add(new(install.Id, "policy-restricted", "Selected content is not admitted by current policy.", "Ask an administrator to review id, source, digest and sequence restrictions; local approval cannot override policy.", true));
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                issues.Add(new(install.Id, "content-unreadable", "Cached content is missing, invalid or differs from its pinned digest.", "Preserve suspect files and reinstall the verified release. Do not approve a changed digest merely to suppress this error.", selected));
            }
            entries.Add(new(install.Id, install.Release, install.Digest, selected, _required.ContainsKey(install.Id), contributions));
        }
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var package in available.Values.Where(package => !rejected.Contains(package.Manifest.Id)))
                foreach (var requirement in package.Manifest.Requires)
                    if (rejected.Contains(requirement.Key) || !available.TryGetValue(requirement.Key, out var dependency) || dependency.Manifest.Release != requirement.Value)
                    {
                        rejected.Add(package.Manifest.Id);
                        issues.Add(new(package.Manifest.Id, "dependency-unavailable", $"Requires admitted {requirement.Key} at release {requirement.Value}.", "Install and approve the dependency in this scope, or resolve its policy/content diagnostics first.", true));
                        changed = true;
                        break;
                    }
        } while (changed);
        try { Order(Bounded(available.Values.Where(package => !rejected.Contains(package.Manifest.Id)))); }
        catch (InvalidDataException)
        {
            issues.Add(new(null, "graph-invalid", "Selected plugin graph contains a cycle or exceeds its content budget.", "Disable unrelated plugins or correct the dependency cycle in a new release.", true));
        }
        return new(issues.All(issue => !issue.Blocking), entries.ToArray(), issues.ToArray());
    }

    public PluginPackage Inspect(string id) => ReadPackage(Find(ReadState(), id));

    public void Install(IEnumerable<PluginPackage> packages, string? registrySource = null)
    {
        if (registrySource is not null) registrySource = PluginRegistry.SourceKey(registrySource);
        using var lease = Lock();
        var state = ReadState();
        var batch = packages.Take(129).ToArray();
        if (batch.Length is 0 or > 128 || batch.Select(package => package.Manifest.Id).Distinct(StringComparer.Ordinal).Count() != batch.Length)
            throw new InvalidDataException("Install needs 1..128 distinct plugins.");
        var next = state.Installed.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var package in batch)
        {
            CheckPolicy(package, registrySource);
            if (next.TryGetValue(package.Manifest.Id, out var current) && current.Release == package.Manifest.Release && current.Digest != package.Digest)
                throw new InvalidDataException("A released plugin is immutable. Increment its release before changing its content.");
            var unchanged = current?.Digest == package.Digest && current.Source == registrySource;
            next[package.Manifest.Id] = new(package.Manifest.Id, package.Manifest.Release, package.Digest, unchanged && current!.Enabled) { Source = registrySource };
            if (!unchanged)
                foreach (var scope in state.Workspaces.Values) scope.Remove(package.Manifest.Id);
        }
        if (next.Count > 128) throw new InvalidDataException("At most 128 plugins can be installed.");
        var supplied = batch.ToDictionary(package => package.Manifest.Id, StringComparer.Ordinal);
        Order(Bounded(next.Values.Select(item => supplied.TryGetValue(item.Id, out var package) ? package : ReadPackage(item))));
        foreach (var package in batch) SavePackage(package);
        SaveState(state with { Installed = next.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList() });
    }

    public void SetEnabled(string id, bool enabled, string? approvedDigest = null, string? workspace = null)
    {
        using var lease = Lock();
        var state = ReadState();
        var install = Find(state, id);
        if (!enabled && _required.ContainsKey(id)) throw new InvalidOperationException("Required plugins cannot be disabled by user or workspace decisions.");
        if (enabled && !string.Equals(install.Digest, approvedDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("Enable requires explicit approval of the installed SHA-256 digest shown by plugins list.");
        if (enabled) CheckPolicy(ReadPackage(install), install.Source);
        if (workspace is null)
            state.Installed[state.Installed.IndexOf(install)] = install with { Enabled = enabled };
        else
        {
            var key = WorkspaceKey(workspace);
            if (!state.Workspaces.TryGetValue(key, out var scope))
            {
                if (state.Workspaces.Count >= 256) throw new InvalidDataException("Plugin workspace limit exceeded.");
                state.Workspaces[key] = scope = new(StringComparer.Ordinal);
            }
            scope[id] = enabled;
        }
        ValidateEnabled(state, null);
        foreach (var scope in state.Workspaces.Keys) ValidateEnabledKey(state, scope);
        SaveState(state);
    }

    public void Remove(string id)
    {
        using var lease = Lock();
        var state = ReadState();
        var install = Find(state, id);
        if (_required.ContainsKey(id)) throw new InvalidOperationException("Required plugins cannot be removed until the administrator releases the requirement.");
        if (state.Installed.Where(item => item.Id != id).Select(ReadPackage).Any(package => package.Manifest.Requires.ContainsKey(id)))
            throw new InvalidOperationException("Another installed plugin requires this plugin. Remove dependants first.");
        state.Installed.Remove(install);
        foreach (var scope in state.Workspaces.Values) scope.Remove(id);
        foreach (var key in state.Usage.Keys.Where(key => key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray()) state.Usage.Remove(key);
        SaveState(state);
    }

    public void RecordUse(string id, string contribution, string digest)
    {
        using var lease = Lock();
        var state = ReadState();
        var install = Find(state, id);
        if (install.Digest != digest || !ReadPackage(install).Manifest.Contributions.Any(item => item.Key == contribution))
            throw new InvalidOperationException("Plugin changed since this contribution was loaded.");
        var key = id + "/" + contribution;
        var previous = state.Usage.GetValueOrDefault(key)?.Invocations ?? 0;
        state.Usage[key] = new(previous == long.MaxValue ? previous : previous + 1, DateTimeOffset.UtcNow);
        SaveState(state);
    }

    private static PluginInstall Find(PluginState state, string id) => state.Installed.SingleOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException("Plugin is not installed.");

    private void CheckPolicy(PluginPackage package, string? source)
    {
        if (_required.TryGetValue(package.Manifest.Id, out var digest) && package.Digest != digest)
            throw new InvalidOperationException($"Plugin {package.Manifest.Id} must match its administrator-reviewed digest.");
        var reason = sourceRestriction?.Invoke(source) ?? restriction?.Invoke(package);
        if (reason is not null) throw new InvalidOperationException($"Plugin {package.Manifest.Id} is restricted: {reason}");
    }

    private static string WorkspaceKey(string workspace)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }

    private bool Enabled(PluginState state, PluginInstall install, string? workspace) => _required.TryGetValue(install.Id, out var digest) ? install.Digest == digest : workspace is not null &&
        state.Workspaces.TryGetValue(WorkspaceKey(workspace), out var scope) && scope.TryGetValue(install.Id, out var enabled) ? enabled : install.Enabled;

    private void ValidateEnabled(PluginState state, string? workspace) =>
        Order(Bounded(state.Installed.Where(item => Enabled(state, item, workspace)).Select(ReadPackage)));

    private void ValidateEnabledKey(PluginState state, string key) =>
        Order(Bounded(state.Installed.Where(item => _required.TryGetValue(item.Id, out var digest) ? item.Digest == digest : state.Workspaces[key].GetValueOrDefault(item.Id, item.Enabled)).Select(ReadPackage)));

    private static IReadOnlyCollection<PluginPackage> Bounded(IEnumerable<PluginPackage> packages)
    {
        long bytes = 0;
        var result = new List<PluginPackage>();
        foreach (var package in packages)
        {
            if ((bytes += package.Bytes) > 67_108_864 || result.Count >= 128)
                throw new InvalidDataException("Plugin graph exceeds its 64 MiB/128 package budget.");
            result.Add(package);
        }
        return result;
    }

    internal static IReadOnlyList<PluginPackage> Order(IReadOnlyCollection<PluginPackage> packages)
    {
        var available = packages.ToDictionary(package => package.Manifest.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PluginPackage>();
        void Visit(PluginPackage package)
        {
            var id = package.Manifest.Id;
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException("Plugin requirements contain a cycle.");
            foreach (var requirement in package.Manifest.Requires.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!available.TryGetValue(requirement.Key, out var dependency) || dependency.Manifest.Release != requirement.Value)
                    throw new InvalidOperationException($"Plugin {id} requires enabled/available {requirement.Key} at exact release {requirement.Value}.");
                Visit(dependency);
            }
            visiting.Remove(id);
            visited.Add(id);
            result.Add(package);
        }
        foreach (var package in packages.OrderBy(package => package.Manifest.Id, StringComparer.Ordinal)) Visit(package);
        return result;
    }

    private FileStream Lock()
    {
        var path = PluginFiles.Confined(DirectoryPath, ".lock");
        Directory.CreateDirectory(DirectoryPath);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private PluginState ReadState()
    {
        var path = PluginFiles.Confined(DirectoryPath, "installed.json");
        if (!File.Exists(path)) return new();
        var bytes = PluginFiles.Read(DirectoryPath, "installed.json", 2_097_152);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            PluginFiles.UniqueProperties(document.RootElement);
            var state = JsonSerializer.Deserialize<PluginState>(bytes, PluginManifest.Json) ?? throw new InvalidDataException("Plugin state is empty.");
            if (state.Schema != 1 || state.Installed is null || state.Workspaces is null || state.Usage is null || state.Installed.Count > 128 || state.Workspaces.Count > 256 ||
                state.Usage.Count > 16_384 || state.Installed.Any(item => item is null || !PluginManifest.ValidId(item.Id) || !PluginManifest.ValidRelease(item.Release) || !ValidDigest(item.Digest)) ||
                state.Installed.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Installed.Count ||
                state.Workspaces.Any(pair => !ValidDigest(pair.Key) || pair.Value is null || pair.Value.Count > 128 || pair.Value.Keys.Any(id => !state.Installed.Any(item => item.Id == id))) ||
                state.Usage.Any(pair => pair.Value is null || pair.Value.Invocations < 0))
                throw new InvalidDataException("Plugin state violates its schema or bounds.");
            foreach (var install in state.Installed)
                if (install.Source is not null && (install.Source.Length > 4096 || PluginRegistry.SourceKey(install.Source) != install.Source))
                    throw new InvalidDataException("Plugin installation provenance must be a canonical registry source.");
            return state;
        }
        catch (JsonException exception) { throw new InvalidDataException("Plugin state is corrupt; no plugins were loaded.", exception); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Plugin state contains an invalid registry source; no plugins were loaded.", exception);
        }
    }

    private PluginPackage ReadPackage(PluginInstall install)
    {
        var package = PluginPackage.Open(PluginFiles.Confined(DirectoryPath, "objects/" + install.Digest));
        if (package.Digest != install.Digest || package.Manifest.Id != install.Id || package.Manifest.Release != install.Release)
            throw new InvalidDataException("Installed plugin content no longer matches its pinned digest and identity.");
        return package;
    }

    private void SavePackage(PluginPackage package)
    {
        var target = PluginFiles.Confined(DirectoryPath, "objects/" + package.Digest);
        if (Directory.Exists(target))
        {
            if (PluginPackage.Open(target).Digest != package.Digest) throw new InvalidDataException("Plugin content cache was modified.");
            return;
        }
        var staging = PluginFiles.Confined(DirectoryPath, "staging/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            package.WriteTo(staging);
            if (PluginPackage.Open(staging).Digest != package.Digest) throw new InvalidDataException("Plugin changed while preparing installation.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private void SaveState(PluginState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, PluginManifest.Json);
        if (bytes.Length > 2_097_152) throw new InvalidDataException("Plugin state exceeds its byte limit.");
        var temporary = PluginFiles.Confined(DirectoryPath, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, PluginFiles.Confined(DirectoryPath, "installed.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool ValidDigest(string? digest) => digest is not null && Regex.IsMatch(digest, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
}