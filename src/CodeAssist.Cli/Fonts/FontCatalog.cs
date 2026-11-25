using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAssist.Cli.Fonts;

public sealed record FontPack(string Archive, string DisplayName, long Size, string? Digest)
{
    public bool IsTextFamily => Archive != "NerdFontsSymbolsOnly.zip";
    public string Url => FontCatalog.ReleaseRoot + Archive;
    public bool Matches(string query) => (DisplayName + " " + Archive).Contains(query, StringComparison.OrdinalIgnoreCase);
    public bool MatchesFamily(string family)
    {
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var actual = Normalize(family.Replace(" Nerd Font Mono", "", StringComparison.Ordinal).Replace(" NFM", "", StringComparison.Ordinal));
        var expected = Normalize(DisplayName);
        return actual.StartsWith(expected, StringComparison.Ordinal);
    }
    public override string ToString() => DisplayName + " / " + Archive + (IsTextFamily ? "" : " / symbols only: not a terminal text font");
}

public static class FontCatalog
{
    public const string Version = "v3.5.1";
    public const string MetadataUrl = "https://api.github.com/repos/ryanoasis/nerd-fonts/releases/tags/" + Version;
    public const string ReleaseRoot = "https://github.com/ryanoasis/nerd-fonts/releases/download/" + Version + "/";
    public const long MaximumArchiveBytes = 768L * 1024 * 1024;
    private const string Names = "0xProto|3270|AdwaitaMono|Agave|AnnotationMono=AnnotationM|AnonymousPro=AnonymicePro|Arimo|AtkinsonHyperlegibleMono=AtkynsonMono|AurulentSansMono=AurulentSansM|BigBlueTerminal=BigBlueTerm|BitstreamVeraSansMono=BitstromWera|IBMPlexMono=BlexMono|CascadiaCode=CaskaydiaCove|CascadiaMono=CaskaydiaMono|CodeNewRoman|ComicShannsMono|CommitMono|Cousine|D2Coding=D2KodingLigature|DaddyTimeMono|DejaVuSansMono=DejaVuSansM|DepartureMono|DroidSansMono=DroidSansM|EnvyCodeR|FantasqueSansMono=FantasqueSansM|FiraCode|FiraMono|GeistMono|Go-Mono=GoMono|Gohu=GohuFont|GoogleSansCode|Hack|Hasklig=Hasklug|HeavyData|Hermit=Hurmit|iA-Writer=iMWriting|Inconsolata|InconsolataGo|InconsolataLGC=Inconsolata LGC|IntelOneMono=IntoneMono|Iosevka|IosevkaTerm|IosevkaTermSlab|JetBrainsMono|Lekton|LiberationMono=LiterationMono|Lilex|MartianMono|Meslo=MesloLG|Monaspace=Monaspice|Monofur|Monoid|Mononoki|MPlus=M+|Noto|OpenDyslexic|Overpass|ProFont|ProggyClean|Recursive=RecMono|RobotoMono|ShareTechMono=ShureTechMono|SourceCodePro=SauceCodePro|SpaceMono|NerdFontsSymbolsOnly=Symbols|Terminus=Terminess|Tinos|Ubuntu|UbuntuMono|UbuntuSans|VictorMono|ZedMono";
    public static IReadOnlyDictionary<string, string> Aliases { get; } = Names.Split('|').Select(value => value.Split('='))
        .ToDictionary(parts => parts[0] + ".zip", parts => parts.Length == 2 ? parts[1] : parts[0], StringComparer.Ordinal);

    public static IReadOnlyList<FontPack> Parse(ReadOnlyMemory<byte> metadata)
    {
        if (metadata.Length > 2 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds size limit.");
        using var document = JsonDocument.Parse(metadata);
        var release = document.RootElement;
        if (release.GetProperty("tag_name").GetString() != Version || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Unexpected official release.");
        var packs = new List<FontPack>();
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.Ordinal) || name == "FontPatcher.zip") continue;
            if (name.Length > 100 || name[..^4].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new InvalidDataException("Unsafe catalog asset name.");
            if (asset.GetProperty("browser_download_url").GetString() != ReleaseRoot + name || asset.GetProperty("state").GetString() != "uploaded")
                throw new InvalidDataException("Untrusted catalog asset.");
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
            if (digest is not null && (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest[7..].Any(character => !Uri.IsHexDigit(character)))) digest = null;
            packs.Add(new FontPack(name, Aliases.GetValueOrDefault(name, name[..^4]), size, digest));
        }
        if (packs.Count is 0 or > 128 || packs.Select(pack => pack.Archive).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packs.Count)
            throw new InvalidDataException("Empty or ambiguous catalog.");
        return packs.OrderBy(pack => pack.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task VerifyArchiveAsync(FontPack pack, Stream input, Stream output, CancellationToken cancellation)
    {
        if (!pack.IsTextFamily || pack.Size is <= 0 or > MaximumArchiveBytes || pack.Digest is null || pack.Digest.Length != 71 || !pack.Digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException("Trusted SHA-256 or bounded text font archive unavailable. ASCII retained.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long consumed = 0;
        while (consumed < pack.Size)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, pack.Size - consumed)), cancellation);
            if (count == 0) throw new InvalidDataException("Incomplete font archive.");
            consumed += count;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellation) != 0 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(pack.Digest[7..])))
            throw new InvalidDataException("Font archive integrity check failed. ASCII retained.");
        output.Position = 0;
    }
}

public sealed record FontInstallation(string Family, byte[] Payload, IReadOnlyDictionary<string, byte[]> Notices);

public static class FontArchive
{
    public const int MaximumFaceBytes = 16 * 1024 * 1024;
    public const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    internal static bool IsSafePath(string path)
    {
        if (path.Length is 0 or > 240 || path.StartsWith('/') || path.Any(character => char.IsControl(character) || "\\:*?\"<>|".Contains(character))) return false;
        foreach (var component in path.TrimEnd('/').Split('/'))
        {
            if (component is "" or "." or ".." || component.EndsWith('.') || component.EndsWith(' ')) return false;
            var stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9') return false;
        }
        return true;
    }
    public static async Task<FontInstallation> ExtractAsync(FontPack pack, Stream verifiedArchive, CancellationToken cancellation)
    {
        using var archive = new ZipArchive(verifiedArchive, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is 0 or > 4096) throw new InvalidDataException("Font archive entry limit exceeded.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = entry.FullName;
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!IsSafePath(path) ||
                kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0 || !names.Add(path))
                throw new InvalidDataException("Unsafe font archive path or link.");
            total = checked(total + entry.Length);
            if (entry.Length > MaximumFaceBytes || total > MaximumExpandedBytes || entry.Length > Math.Max(1, entry.CompressedLength) * 200)
                throw new InvalidDataException("Font archive decompression limit exceeded.");
        }
        var notices = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var noticeBytes = 0L;
        FontInstallation? selected = null;
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var name = entry.Name;
            var font = name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
            var notice = name.Contains("license", StringComparison.OrdinalIgnoreCase) || name.Contains("notice", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("copyright", StringComparison.OrdinalIgnoreCase) || name.Equals("OFL", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            if (!font && !notice) continue;
            if (notice && entry.Length > 1024 * 1024) throw new InvalidDataException("Font notice exceeds limit.");
            await using var input = entry.Open();
            var bytes = new byte[(int)entry.Length];
            await input.ReadExactlyAsync(bytes, cancellation);
            if (await input.ReadAsync(new byte[1], cancellation) != 0) throw new InvalidDataException("Font entry length mismatch.");
            if (notice)
            {
                noticeBytes += bytes.Length;
                if (noticeBytes > 8 * 1024 * 1024) throw new InvalidDataException("Font attribution aggregate limit exceeded.");
                notices.Add("archive/" + entry.FullName, bytes);
            }
            if (font)
            {
                using var face = new MemoryStream(bytes, writable: false);
                var family = TrueTypeFont.Describe(face, strict: true);
                if (family is not null && pack.MatchesFamily(family) && selected is null) selected = new(family, bytes, notices);
            }
        }
        if (selected is null) throw new InvalidDataException("No compatible regular Nerd Font Mono face with text and required glyphs. ASCII retained.");
        if (!notices.Any(notice => notice.Value.Length > 0 && (notice.Key.Contains("license", StringComparison.OrdinalIgnoreCase) || notice.Key.Contains("copyright", StringComparison.OrdinalIgnoreCase) || notice.Key.Contains("ofl", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Selected font license missing. ASCII retained.");
        return selected;
    }
}