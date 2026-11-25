using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAssist.Extensions.Plugins;

public sealed class PluginPackage
{
    private readonly Dictionary<string, byte[]> _files;
    private readonly PluginManifest _manifest;
    public PluginManifest Manifest => _manifest with
    {
        Requires = new(_manifest.Requires, StringComparer.Ordinal),
        Assets = [.. _manifest.Assets],
        Contributions = _manifest.Contributions.Select(item => item with { Tools = [.. item.Tools] }).ToList(),
    };
    public string Digest { get; }
    public long Bytes => _files.Values.Sum(bytes => (long)bytes.Length);

    private PluginPackage(Dictionary<string, byte[]> files)
    {
        if (!files.TryGetValue(PluginManifest.FileName, out var manifest) || manifest.Length > 131_072)
            throw new InvalidDataException("Package needs a bounded assist-plugin.json at its root.");
        _manifest = PluginManifest.Parse(manifest);
        var declared = _manifest.Contributions.Select(item => item.Entry).Concat(_manifest.Assets).Append(PluginManifest.FileName).ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(files.Keys) || files.Count > 385 || files.Values.Any(bytes => bytes.Length > PluginFiles.MaxFileBytes) ||
            files.Values.Sum(bytes => (long)bytes.Length) > PluginFiles.MaxPackageBytes)
            throw new InvalidDataException("Package files must exactly match the manifest and fit the file/total byte limits.");
        foreach (var contribution in _manifest.Contributions)
        {
            var text = PluginFiles.Text(files[contribution.Entry]);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Plugin contributions cannot be empty.");
            if (contribution.Entry.EndsWith(".json", StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Structured plugin contributions must be JSON objects.");
                    PluginFiles.UniqueProperties(document.RootElement);
                    PluginDefinitions.Validate(contribution, text);
                }
                catch (JsonException exception) { throw new InvalidDataException("Plugin contribution contains invalid JSON.", exception); }
            }
        }
        _files = files;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{pair.Key.Length}:{pair.Key}:{pair.Value.Length}:"));
            hash.AppendData(pair.Value);
        }
        Digest = Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static PluginPackage Open(string directory)
    {
        var bytes = PluginFiles.Read(directory, PluginManifest.FileName, 131_072);
        var manifest = PluginManifest.Parse(bytes);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PluginManifest.FileName] = bytes };
        long total = bytes.Length;
        foreach (var relative in manifest.Contributions.Select(item => item.Entry).Concat(manifest.Assets))
        {
            var content = PluginFiles.Read(directory, relative);
            total += content.Length;
            if (total > PluginFiles.MaxPackageBytes) throw new InvalidDataException("Plugin exceeds its total byte limit.");
            files.Add(relative, content);
        }
        return new(files);
    }

    public static PluginPackage Scaffold(string directory, string id)
    {
        if (!PluginManifest.ValidId(id)) throw new InvalidDataException("Plugin id must be lowercase and dotted, for example team.review.");
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Plugin creation requires a new directory; existing files are never overwritten.");
        var manifest = new PluginManifest
        {
            Id = id, Release = "0.1.0", Summary = "Scoped review guidance",
            Contributions = [new() { Key = "inspect", Kind = "prompt", Entry = "guidance/inspect.md", Description = "Inspect a scoped code change", Tools = ["fs.read", "fs.find"] }],
        };
        Directory.CreateDirectory(Path.Combine(directory, "guidance"));
        File.WriteAllBytes(Path.Combine(directory, PluginManifest.FileName), JsonSerializer.SerializeToUtf8Bytes(manifest, PluginManifest.Json));
        File.WriteAllText(Path.Combine(directory, "guidance", "inspect.md"),
            "Inspect only the requested change. Cite concrete evidence and separate defects from suggestions. Do not execute changes without the user's approval.\n", new UTF8Encoding(false));
        return Open(directory);
    }

    public static PluginPackage FromArchive(string archive)
    {
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromArchive(stream);
    }

    public static PluginPackage FromArchive(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > PluginFiles.MaxPackageBytes + 1_048_576)
            throw new InvalidDataException("Plugin archives must be seekable and bounded to 17 MiB.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 385) throw new InvalidDataException("Plugin archive has too many entries.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            PluginFiles.Relative(entry.FullName);
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode is not (0 or 0x8000) || entry.Length > PluginFiles.MaxFileBytes ||
                (total += entry.Length) > PluginFiles.MaxPackageBytes)
                throw new InvalidDataException("Plugin archives accept bounded regular files only.");
            using var input = entry.Open();
            var bytes = new byte[checked((int)entry.Length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || !files.TryAdd(entry.FullName, bytes))
                throw new InvalidDataException("Plugin archive contains duplicate paths or invalid file lengths.");
        }
        return new(files.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public void Pack(string destination)
    {
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var pair in _files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var output = entry.Open();
            output.Write(pair.Value);
        }
    }

    public string Text(string entry) => PluginFiles.Text(_files.TryGetValue(entry, out var bytes)
        ? bytes : throw new KeyNotFoundException("File is not declared by this plugin."));

    public int FileBytes(string entry) => _files.TryGetValue(entry, out var bytes) ? bytes.Length :
        throw new KeyNotFoundException("File is not declared by this plugin.");

    internal void WriteTo(string directory)
    {
        foreach (var pair in _files)
        {
            var target = PluginFiles.Confined(directory, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(pair.Value);
        }
    }
}