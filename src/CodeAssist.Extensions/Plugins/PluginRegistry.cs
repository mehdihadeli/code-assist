using System.Security.Cryptography;
using System.Text.Json;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Extensions.Plugins;

public sealed record PluginRelease(string Id, string Release, string Archive, string Sha256)
{
    public string Lane { get; init; } = "general";
    public string Blurb { get; init; } = "";
    public List<string> Labels { get; init; } = [];
    public PluginFit Fit { get; init; } = new();
}

public sealed record PluginRegistryDocument
{
    public int Schema { get; init; } = 1;
    public string Name { get; init; } = "";
    public List<PluginRelease> Releases { get; init; } = [];
}

public sealed class PluginRegistry
{
    public const string FileName = "assist-registry.json";
    private const int MaxArchiveBytes = 17_825_792;
    private static readonly HttpClient Http = new(NetworkGuard.CreateHandler(allowPrivate: false));
    private readonly string? _directory;
    private readonly Uri? _address;
    private readonly string? _file;
    private readonly PluginRegistryDocument _document;
    public PluginRegistryDocument Document => _document with { Releases = _document.Releases.Select(Snapshot).ToList() };
    public string Source => _address?.AbsoluteUri ?? SourceKey(_file!);

    public IReadOnlyList<PluginRelease> Search(string query = "", string lane = "general")
    {
        if (query.Length > 256 || !ValidLane(lane)) throw new InvalidDataException("Search requires a query up to 256 characters and a valid release lane.");
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _document.Releases.Where(item => item.Lane == lane).GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => Version.Parse(item.Release)).First())
            .Where(item => words.All(word => (item.Id + " " + item.Blurb + " " + string.Join(" ", item.Labels)).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Id, StringComparer.Ordinal).Select(Snapshot).ToArray();
    }

    public static string SourceKey(string source)
    {
        if (source.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("Plugin registry URLs must use HTTPS without embedded credentials or fragments.");
            return uri.AbsoluteUri;
        }
        var path = Path.GetFullPath(Directory.Exists(source) ? Path.Combine(source, FileName) : source);
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private PluginRegistry(PluginRegistryDocument document, string? directory, Uri? address, string? file = null)
    {
        _document = document;
        _directory = directory;
        _address = address;
        _file = file;
    }

    public static async Task<PluginRegistry> OpenAsync(string source, CancellationToken cancellationToken = default)
    {
        if (source.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var address) || address.Scheme != "https" || address.UserInfo.Length != 0 || address.Fragment.Length != 0)
                throw new InvalidDataException("Plugin registry URLs must use HTTPS without embedded credentials or fragments.");
            return new(Parse(await FetchAsync(address, 131_072, cancellationToken)), null, address);
        }
        var path = Path.GetFullPath(Directory.Exists(source) ? Path.Combine(source, FileName) : source);
        var directory = Path.GetDirectoryName(path)!;
        return new(Parse(PluginFiles.Read(directory, Path.GetFileName(path), 131_072)), directory, null, path);
    }

    public async Task<IReadOnlyList<PluginPackage>> ResolveAsync(string id, string? release = null, CancellationToken cancellationToken = default, string lane = "general")
    {
        if (!PluginManifest.ValidId(id) || release is not null && !PluginManifest.ValidRelease(release) || !ValidLane(lane))
            throw new InvalidDataException("Choose a plugin id and an exact stable release, or omit release to select the latest listed version.");
        var selected = new Dictionary<string, PluginPackage>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        long contentBytes = 0;
        async Task Visit(string requested, string? exact)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visiting.Contains(requested)) throw new InvalidDataException("Registry dependency graph contains a cycle.");
            if (selected.TryGetValue(requested, out var previous))
            {
                if (exact is not null && previous.Manifest.Release != exact) throw new InvalidOperationException("Registry dependency requirements conflict.");
                return;
            }
            if (selected.Count >= 64 || visiting.Count >= 32) throw new InvalidDataException("Registry resolution exceeds its plugin/depth limit.");
            var entry = _document.Releases.Where(item => item.Id == requested && item.Lane == lane && (exact is null || item.Release == exact))
                .OrderByDescending(item => Version.Parse(item.Release)).FirstOrDefault() ?? throw new KeyNotFoundException($"Registry has no matching release of {requested}.");
            var archive = _directory is not null ? PluginFiles.Read(_directory, entry.Archive, MaxArchiveBytes) :
                await FetchAsync(new Uri(_address!, entry.Archive), MaxArchiveBytes, cancellationToken);
            if ((bytes += archive.Length) > 67_108_864) throw new InvalidDataException("Registry resolution exceeds its 64 MiB transfer budget.");
            if (Convert.ToHexStringLower(SHA256.HashData(archive)) != entry.Sha256) throw new InvalidDataException("Plugin archive checksum does not match the reviewed registry.");
            using var stream = new MemoryStream(archive, writable: false);
            var package = PluginPackage.FromArchive(stream);
            if ((contentBytes += package.Bytes) > 67_108_864) throw new InvalidDataException("Registry resolution exceeds its 64 MiB content budget.");
            if (package.Manifest.Id != entry.Id || package.Manifest.Release != entry.Release)
                throw new InvalidDataException("Registry entry and plugin identity/release disagree.");
            visiting.Add(requested);
            selected.Add(requested, package);
            foreach (var requirement in package.Manifest.Requires.OrderBy(pair => pair.Key, StringComparer.Ordinal)) await Visit(requirement.Key, requirement.Value);
            visiting.Remove(requested);
        }
        await Visit(id, release);
        return PluginStore.Order(selected.Values.ToArray());
    }

    public static void Publish(PluginPackage package, string directory, string name = "local.registry", string lane = "general")
    {
        if (!PluginManifest.ValidId(name) || !ValidLane(lane)) throw new InvalidDataException("Registry needs a lowercase dotted name and a valid release lane.");
        var root = Path.GetFullPath(directory);
        var lockPath = PluginFiles.Confined(root, ".registry.lock");
        Directory.CreateDirectory(root);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var document = File.Exists(Path.Combine(root, FileName)) ? Parse(PluginFiles.Read(root, FileName, 131_072)) : new PluginRegistryDocument { Name = name };
        if (document.Releases.Any(item => item.Id == package.Manifest.Id && item.Release == package.Manifest.Release))
            throw new InvalidOperationException("Registry already contains this immutable plugin release.");
        var relative = "releases/" + package.Manifest.Id + "-" + package.Manifest.Release + ".zip";
        var archive = PluginFiles.Confined(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var staging = PluginFiles.Confined(root, "publish-" + Guid.NewGuid().ToString("N") + ".zip");
        var temporary = PluginFiles.Confined(root, "catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
        var moved = false;
        var committed = false;
        try
        {
            package.Pack(staging);
            var digest = Convert.ToHexStringLower(SHA256.HashData(PluginFiles.Read(root, Path.GetFileName(staging), MaxArchiveBytes)));
            document.Releases.Add(new(package.Manifest.Id, package.Manifest.Release, relative, digest) { Lane = lane, Blurb = package.Manifest.Summary });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, PluginManifest.Json);
            Parse(bytes);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, archive, overwrite: false);
            moved = true;
            File.Move(temporary, PluginFiles.Confined(root, FileName), overwrite: true);
            committed = true;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            if (File.Exists(temporary)) File.Delete(temporary);
            if (moved && !committed) File.Delete(archive);
        }
    }

    private static PluginRegistryDocument Parse(byte[] bytes)
    {
        if (bytes.Length > 131_072) throw new InvalidDataException("Plugin registry exceeds its byte limit.");
        try
        {
            using var json = JsonDocument.Parse(bytes);
            PluginFiles.UniqueProperties(json.RootElement);
            var document = JsonSerializer.Deserialize<PluginRegistryDocument>(bytes, PluginManifest.Json) ?? throw new InvalidDataException("Plugin registry is empty.");
            if (document.Schema != 1 || !PluginManifest.ValidId(document.Name) || document.Releases is null || document.Releases.Count > 512 ||
                document.Releases.Any(item => item is null || !PluginManifest.ValidId(item.Id) || !PluginManifest.ValidRelease(item.Release) || !PluginStore.ValidDigest(item.Sha256)) ||
                document.Releases.Select(item => item.Id + "@" + item.Release).Distinct(StringComparer.Ordinal).Count() != document.Releases.Count)
                throw new InvalidDataException("Plugin registry has invalid identity, duplicate releases or invalid checksums.");
            foreach (var release in document.Releases)
            {
                if (!ValidLane(release.Lane) || release.Blurb is null || release.Blurb.Length > 1024 || release.Blurb.Any(char.IsControl) ||
                    release.Labels is null || release.Labels.Count > 24 || release.Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 64 || label.Any(char.IsControl)) || release.Fit is null)
                    throw new InvalidDataException("Registry release metadata violates its lane, text or label limits.");
                release.Fit.Validate();
                PluginFiles.Relative(release.Archive);
                if (!release.Archive.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("Registry sources must be relative ZIP archives.");
            }
            return document;
        }
        catch (JsonException exception) { throw new InvalidDataException("Plugin registry contains invalid or unknown JSON fields.", exception); }
    }

    private static bool ValidLane(string? lane) => lane is not null && System.Text.RegularExpressions.Regex.IsMatch(lane, "^[a-z][a-z0-9-]{0,31}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static PluginRelease Snapshot(PluginRelease release) => release with { Labels = [.. release.Labels], Fit = release.Fit.Copy() };

    private static async Task<byte[]> FetchAsync(Uri address, int limit, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Plugin registry download failed or exceeded its byte limit. Redirects are not followed.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, deadline.Token);
            if (count == 0) break;
            if (output.Length + count > limit) throw new InvalidDataException("Plugin download exceeded its byte limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}