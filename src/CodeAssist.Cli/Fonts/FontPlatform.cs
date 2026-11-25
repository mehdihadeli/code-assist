using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodeAssist.Cli.Fonts;

public enum FontPlatform { Windows, MacOS, Linux, Unsupported }

public interface IFontFiles
{
    bool Exists(string path);
    Stream OpenRead(string path);
    IEnumerable<string> EnumerateFonts(string directory);
    void CreateDirectory(string path);
    void WriteNew(string path, byte[] bytes);
    void MoveNew(string source, string destination);
    void Delete(string path);
    void WriteAtomic(string path, byte[] bytes);
    Task RefreshAsync(FontPlatform platform, string path, CancellationToken cancellation);
}

public sealed record FontPaths(FontPlatform Platform, string TargetDirectory, IReadOnlyList<string> SearchDirectories)
{
    public const string FileName = "CodeAssist-JetBrainsMonoNFM-fa7b8599.ttf";

    public static FontPaths For(FontPlatform platform, string home, string localData, string? xdgData = null) => platform switch
    {
        FontPlatform.Windows => new(platform, Path.Combine(localData, "Microsoft", "Windows", "Fonts"),
            [Path.Combine(localData, "Microsoft", "Windows", "Fonts"), Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "Fonts")]),
        FontPlatform.MacOS => new(platform, Path.Combine(home, "Library", "Fonts"),
            [Path.Combine(home, "Library", "Fonts"), "/Library/Fonts", "/System/Library/Fonts"]),
        FontPlatform.Linux => new(platform, Path.Combine(!string.IsNullOrEmpty(xdgData) && Path.IsPathFullyQualified(xdgData) ? xdgData : Path.Combine(home, ".local", "share"), "fonts", "codeassist"),
            [Path.Combine(!string.IsNullOrEmpty(xdgData) && Path.IsPathFullyQualified(xdgData) ? xdgData : Path.Combine(home, ".local", "share"), "fonts"), Path.Combine(home, ".fonts"), "/usr/local/share/fonts", "/usr/share/fonts"]),
        _ => new(platform, "", [])
    };
}

public sealed class FontPreferences(IFontFiles files, string directory, string terminalIdentity) : IFontPreferences
{
    public static string Confirm(string family) => "v2:nerd:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(family));
    public static string? ConfirmedFamily(string? value)
    {
        if (value == "v1:nerd") return FontPin.Family;
        if (value is null || value.Length > 512 || !value.StartsWith("v2:nerd:", StringComparison.Ordinal)) return null;
        try
        {
            var family = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value[8..]));
            return family.Length is > 0 and <= 160 && !family.Any(char.IsControl) ? family : null;
        }
        catch (ArgumentException) { return null; }
        catch (FormatException) { return null; }
    }
    public string PathName { get; } = Path.Combine(directory, "terminal-icons-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(terminalIdentity)))[..16] + ".state");

    public string? Read()
    {
        if (!files.Exists(PathName)) return null;
        using var stream = files.OpenRead(PathName);
        Span<byte> bytes = stackalloc byte[513];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 512) return "v1:ascii";
        var value = Encoding.ASCII.GetString(bytes[..count]);
        return value is "v1:nerd" or "v1:ascii" || ConfirmedFamily(value) is not null ? value : "v1:ascii";
    }

    public void Write(string value)
    {
        if (value is not ("v1:nerd" or "v1:ascii") && ConfirmedFamily(value) is null) throw new ArgumentException("Invalid icon preference.", nameof(value));
        files.CreateDirectory(Path.GetDirectoryName(PathName)!);
        files.WriteAtomic(PathName, Encoding.ASCII.GetBytes(value));
    }
}

public sealed class FontHost(IFontFiles files, FontPaths paths, bool isLocal, FontPin pin) : IFontHost
{
    public bool IsLocal { get; } = isLocal && paths.Platform != FontPlatform.Unsupported;

    public static bool IsLocalEnvironment(IReadOnlyDictionary<string, string> environment, bool redirected, bool userInteractive, bool containerFile)
    {
        if (redirected || !userInteractive || containerFile) return false;
        string? Value(string key) => environment.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        if (new[] { "SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY", "WSL_DISTRO_NAME", "WSL_INTEROP", "container" }.Any(key => !string.IsNullOrEmpty(Value(key)))) return false;
        if (new[] { "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_RUNNING_IN_CONTAINERS", "CI" }.Any(key => Value(key) == "1" || string.Equals(Value(key), "true", StringComparison.OrdinalIgnoreCase))) return false;
        return !string.Equals(Value("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> IsInstalledAsync(CancellationToken cancellation)
    {
        foreach (var directory in paths.SearchDirectories)
        {
            try
            {
                foreach (var path in files.EnumerateFonts(directory).Take(2048))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = files.OpenRead(path);
                        if (TrueTypeFont.IsCompatible(stream)) return Task.FromResult(true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return Task.FromResult(false);
    }

    public Task<string?> FindInstalledAsync(string? family, FontPack? pack, CancellationToken cancellation)
    {
        foreach (var directory in paths.SearchDirectories)
        {
            try
            {
                foreach (var path in files.EnumerateFonts(directory).Take(4096))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = files.OpenRead(path);
                        var actual = TrueTypeFont.Describe(stream, strict: true);
                        if (actual is not null && (family is null || actual == family) && (pack is null || pack.MatchesFamily(actual)))
                            return Task.FromResult<string?>(actual);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return Task.FromResult<string?>(null);
    }

    public async Task RegisterAsync(FontInstallation installation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsLocal) throw new IOException("Font registration requires a local interactive host.");
        using var face = new MemoryStream(installation.Payload, writable: false);
        if (TrueTypeFont.Describe(face, strict: true) != installation.Family || installation.Notices.Count == 0 ||
            installation.Notices.Any(notice => !FontArchive.IsSafePath(notice.Key) || notice.Value.Length is 0 or > 1024 * 1024) ||
            installation.Notices.Sum(notice => (long)notice.Value.Length) > 16 * 1024 * 1024)
            throw new InvalidDataException("Font family, compatibility or attribution unavailable.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(installation.Payload));
        var destination = Path.Combine(paths.TargetDirectory, "CodeAssist-" + hash + ".ttf");
        var noticeDirectory = destination + ".notices";
        var created = new List<string>();
        files.CreateDirectory(paths.TargetDirectory);
        try
        {
            foreach (var notice in installation.Notices)
            {
                var components = notice.Key.Split('/');
                var noticePath = Path.Combine(noticeDirectory, Path.Combine(components));
                files.CreateDirectory(Path.GetDirectoryName(noticePath)!);
                await WriteMatchingAsync(noticePath, notice.Value, created, cancellation);
            }
            await WriteMatchingAsync(destination, installation.Payload, created, cancellation);
            await files.RefreshAsync(paths.Platform, destination, cancellation);
        }
        catch
        {
            foreach (var path in created.AsEnumerable().Reverse()) TryDelete(path);
            throw;
        }
    }

    private async Task WriteMatchingAsync(string destination, byte[] payload, List<string> created, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (files.Exists(destination))
        {
            await using var existing = files.OpenRead(destination);
            if (existing.Length != payload.Length || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(existing, cancellation), SHA256.HashData(payload)))
                throw new InvalidDataException("Preexisting font or attribution will not be overwritten.");
            return;
        }
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".codeassist-font-" + Guid.NewGuid().ToString("N") + ".tmp");
        var written = false;
        try
        {
            files.WriteNew(temporary, payload);
            written = true;
            cancellation.ThrowIfCancellationRequested();
            files.MoveNew(temporary, destination);
            written = false;
            created.Add(destination);
        }
        finally { if (written) TryDelete(temporary); }
    }

    public async Task RegisterAsync(byte[] payload, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsLocal) throw new InvalidOperationException("Font registration requires a local interactive host.");
        await using (var stream = new MemoryStream(payload, writable: false))
            await pin.VerifyAsync(stream, cancellation);
        files.CreateDirectory(paths.TargetDirectory);
        var destination = Path.Combine(paths.TargetDirectory, FontPaths.FileName);
        var temporary = Path.Combine(paths.TargetDirectory, ".codeassist-font-" + Guid.NewGuid().ToString("N") + ".tmp");
        var createdTemporary = false;
        var createdDestination = false;
        try
        {
            if (files.Exists(destination))
            {
                await using var existing = files.OpenRead(destination);
                await pin.VerifyAsync(existing, cancellation);
            }
            else
            {
                files.WriteNew(temporary, payload);
                createdTemporary = true;
                cancellation.ThrowIfCancellationRequested();
                files.MoveNew(temporary, destination);
                createdTemporary = false;
                createdDestination = true;
            }
            await files.RefreshAsync(paths.Platform, destination, cancellation);
        }
        catch
        {
            if (createdDestination) TryDelete(destination);
            throw;
        }
        finally
        {
            if (createdTemporary) TryDelete(temporary);
        }
    }

    private void TryDelete(string path)
    {
        try { files.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

public static class TrueTypeFont
{
    public static IReadOnlyList<int> RequiredGlyphs { get; } = Array.AsReadOnly(new[] { 0xeb08, 0xea76, 0xea60, 0xec0f, 0xf15c, 0xf044, 0xf489, 0xf07b });

    public static bool IsCompatible(Stream stream)
        => Describe(stream) == FontPin.Family;

    public static string? Describe(Stream stream, bool strict = false)
    {
        try
        {
            if (!stream.CanSeek || stream.Length is < 12 or > 16 * 1024 * 1024) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (Read32(bytes, 0) is not (0x00010000 or 0x4f54544f)) return null;
            var count = Read16(bytes, 4);
            if (count is 0 or > 128 || 12 + count * 16 > bytes.Length) return null;
            (int Offset, int Length) Table(string tag)
            {
                for (var index = 0; index < count; index++)
                {
                    var entry = 12 + index * 16;
                    if (Encoding.ASCII.GetString(bytes, entry, 4) != tag) continue;
                    var offset = checked((int)Read32(bytes, entry + 8));
                    var length = checked((int)Read32(bytes, entry + 12));
                    if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new InvalidDataException();
                    return (offset, length);
                }
                throw new InvalidDataException();
            }
            var post = Table("post");
            if (post.Length < 16 || Read32(bytes, post.Offset + 12) == 0) return null;
            var names = Table("name");
            var strings = names.Offset + Read16(bytes, names.Offset + 4);
            var nameCount = Read16(bytes, names.Offset + 2);
            string? family = null;
            string? style = null;
            if (names.Length < 6 || 6 + nameCount * 12 > names.Length) return null;
            for (var index = 0; index < nameCount; index++)
            {
                var entry = names.Offset + 6 + index * 12;
                if (Read16(bytes, entry) is not (0 or 3)) continue;
                var position = strings + Read16(bytes, entry + 10);
                var length = Read16(bytes, entry + 8);
                if (position < names.Offset || position > names.Offset + names.Length - length || length % 2 != 0) return null;
                var name = Encoding.BigEndianUnicode.GetString(bytes, position, length);
                var identifier = Read16(bytes, entry + 6);
                if (identifier is 2 or 17 && (style is null || identifier == 17)) style = name;
                if (identifier is 1 or 16 && (family is null || identifier == 16) &&
                    (name.EndsWith(" Nerd Font Mono", StringComparison.Ordinal) || name.EndsWith(" NFM", StringComparison.Ordinal))) family = name;
            }
            if (family is null || family.Length > 160 || family.Any(char.IsControl)) return null;
            if (strict && style is not ("Regular" or "Book" or "Normal")) return null;
            var cmap = Table("cmap");
            var subtables = Read16(bytes, cmap.Offset + 2);
            var covered = new HashSet<int>();
            for (var index = 0; index < subtables; index++)
            {
                var entry = cmap.Offset + 4 + index * 8;
                var platform = Read16(bytes, entry);
                var encoding = Read16(bytes, entry + 2);
                if (platform != 0 && !(platform == 3 && encoding is 1 or 10)) continue;
                var offset = checked(cmap.Offset + (int)Read32(bytes, entry + 4));
                var format = Read16(bytes, offset);
                foreach (var glyph in RequiredGlyphs)
                {
                    if (format == 12)
                    {
                        var groups = Read32(bytes, offset + 12);
                        if (groups > bytes.Length / 12) return null;
                        for (var group = 0; group < groups; group++)
                        {
                            var start = offset + 16 + group * 12;
                            var first = Read32(bytes, start);
                            var last = Read32(bytes, start + 4);
                            if (glyph >= first && glyph <= last && Read32(bytes, start + 8) + glyph - first != 0) covered.Add(glyph);
                        }
                    }
                    else if (format == 4)
                    {
                        var segments = Read16(bytes, offset + 6) / 2;
                        var ends = offset + 14;
                        var starts = ends + segments * 2 + 2;
                        var deltas = starts + segments * 2;
                        var ranges = deltas + segments * 2;
                        for (var segment = 0; segment < segments; segment++)
                        {
                            var first = Read16(bytes, starts + segment * 2);
                            if (glyph < first || glyph > Read16(bytes, ends + segment * 2)) continue;
                            var range = Read16(bytes, ranges + segment * 2);
                            var glyphIndex = range == 0 ? glyph : Read16(bytes, ranges + segment * 2 + range + (glyph - first) * 2);
                            if ((range == 0 || glyphIndex != 0) && ((glyphIndex + Read16(bytes, deltas + segment * 2)) & 0xffff) != 0) covered.Add(glyph);
                        }
                    }
                }
            }
            if (!RequiredGlyphs.All(covered.Contains)) return null;
            if (strict)
            {
                var head = Table("head");
                var maximum = Table("maxp");
                var horizontal = Table("hhea");
                var metrics = Table("hmtx");
                if (head.Length < 54 || Read32(bytes, head.Offset + 12) != 0x5f0f3cf5 || maximum.Length < 6 || horizontal.Length < 36) return null;
                var glyphCount = Read16(bytes, maximum.Offset + 4);
                var metricCount = Read16(bytes, horizontal.Offset + 34);
                if (glyphCount == 0 || metricCount == 0 || metricCount > glyphCount || metrics.Length < metricCount * 4 + (glyphCount - metricCount) * 2) return null;
                int GlyphIndex(int character)
                {
                    for (var subtable = 0; subtable < subtables; subtable++)
                    {
                        var entry = cmap.Offset + 4 + subtable * 8;
                        var platform = Read16(bytes, entry);
                        var encoding = Read16(bytes, entry + 2);
                        if (platform != 0 && !(platform == 3 && encoding is 1 or 10)) continue;
                        var offset = checked(cmap.Offset + (int)Read32(bytes, entry + 4));
                        if (offset < cmap.Offset || offset > cmap.Offset + cmap.Length - 2) return 0;
                        var format = Read16(bytes, offset);
                        if (format == 12)
                        {
                            var length = checked((int)Read32(bytes, offset + 4));
                            var groups = Read32(bytes, offset + 12);
                            if (length < 16 || offset > cmap.Offset + cmap.Length - length || groups > (length - 16) / 12) return 0;
                            for (var group = 0; group < groups; group++)
                            {
                                var position = offset + 16 + group * 12;
                                var first = Read32(bytes, position);
                                var last = Read32(bytes, position + 4);
                                if (first > last || last > 0x10ffff) return 0;
                                if (character >= first && character <= last) return checked((int)(Read32(bytes, position + 8) + character - first));
                            }
                        }
                        else if (format == 4)
                        {
                            var length = Read16(bytes, offset + 2);
                            var segments = Read16(bytes, offset + 6) / 2;
                            if (length < 16 || offset > cmap.Offset + cmap.Length - length || segments == 0 || 16 + segments * 8 > length) return 0;
                            var ends = offset + 14;
                            var starts = ends + segments * 2 + 2;
                            var deltas = starts + segments * 2;
                            var ranges = deltas + segments * 2;
                            for (var segment = 0; segment < segments; segment++)
                            {
                                var first = Read16(bytes, starts + segment * 2);
                                if (character < first || character > Read16(bytes, ends + segment * 2)) continue;
                                var range = Read16(bytes, ranges + segment * 2);
                                var position = ranges + segment * 2 + range + (character - first) * 2;
                                if (range != 0 && position > offset + length - 2) return 0;
                                var mapped = range == 0 ? character : Read16(bytes, position);
                                return range != 0 && mapped == 0 ? 0 : (mapped + Read16(bytes, deltas + segment * 2)) & 0xffff;
                            }
                        }
                    }
                    return 0;
                }
                int? width = null;
                foreach (var character in Enumerable.Range(32, 95).Concat(RequiredGlyphs))
                {
                    var glyph = GlyphIndex(character);
                    if (glyph <= 0 || glyph >= glyphCount) return null;
                    var advance = Read16(bytes, metrics.Offset + Math.Min(glyph, metricCount - 1) * 4);
                    if (advance == 0 || width is not null && advance != width) return null;
                    width = advance;
                }
                _ = Table(Read32(bytes, 0) == 0x4f54544f ? "CFF " : "glyf");
            }
            return family;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static ushort Read16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
    private static uint Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
}