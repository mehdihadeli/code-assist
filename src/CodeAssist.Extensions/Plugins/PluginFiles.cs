using System.Text;
using System.Text.Json;

namespace CodeAssist.Extensions.Plugins;

internal static class PluginFiles
{
    internal const int MaxFileBytes = 1_048_576;
    internal const int MaxPackageBytes = 16_777_216;

    internal static void Relative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':') ||
            Path.IsPathRooted(path) || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') || part.Any(character => character < 32 || "<>\"|?*".Contains(character))))
            throw new InvalidDataException("Plugin paths must be portable, relative and confined to the package.");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin paths cannot use reserved device names.");
        }
    }

    internal static string Confined(string directory, string relative)
    {
        Relative(relative);
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Plugin path escapes its package.");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin paths cannot traverse symbolic links or junctions.");
            if (current.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        return path;
    }

    internal static byte[] Read(string directory, string relative, int limit = MaxFileBytes)
    {
        var path = Confined(directory, relative);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException("Plugin file exceeds its byte limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static string Text(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Plugin text must be UTF-8.", exception); }
    }

    internal static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON properties are not allowed in plugin documents.");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
}