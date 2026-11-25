using System.Security.Cryptography;
using System.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Extensions.Skills;

public sealed record LocalSkillSource(string Key, string Directory, string? Scope = null);
public sealed record SkillDiagnostic(string Origin, string Path, string Code, string Message);
public sealed record SkillDiscovery(IReadOnlyList<SkillSummary> Skills, IReadOnlyList<SkillDiagnostic> Issues);

public sealed class LocalSkillCatalog : ISkillCatalog
{
    private sealed record Skill(string File, string Directory, string Fallback, SkillSummary Summary);
    private readonly LocalSkillSource[] _sources;
    private readonly Func<SkillSummary, ToolContext?, SkillCaller, bool>? _admission;

    public LocalSkillCatalog(IEnumerable<string> directories) : this(directories.Select((path, index) => new LocalSkillSource("source-" + index, path))) { }

    public LocalSkillCatalog(IEnumerable<LocalSkillSource> sources, Func<SkillSummary, ToolContext?, SkillCaller, bool>? admission = null)
    {
        _sources = sources.Take(65).Select(source => source with { Directory = Path.GetFullPath(source.Directory), Scope = source.Scope is null ? null : Path.GetFullPath(source.Scope) }).ToArray();
        if (_sources.Length > 64 || _sources.Any(source => !SkillDocument.ValidId(source.Key)) || _sources.Select(source => source.Key).Distinct(StringComparer.Ordinal).Count() != _sources.Length)
            throw new ArgumentException("Skills require at most 64 distinctly named sources.");
        _admission = admission;
    }

    public IReadOnlyList<SkillSummary> Index => Discover().Skills.Where(summary => Allowed(summary, null, SkillCaller.Agent)).ToArray();

    public IReadOnlyList<SkillSummary> Describe(ToolContext context, SkillCaller caller = SkillCaller.Agent) => Discover(context).Skills.Where(summary => Allowed(summary, context, caller)).ToArray();

    public SkillDiscovery Discover(ToolContext? context = null)
    {
        var (skills, issues) = Scan(context);
        return new(skills.Where(pair => !pair.Key.Contains('/') || !skills.TryGetValue(pair.Key.Split('/').Last(), out var preferred) || preferred.File != pair.Value.File)
            .Select(pair => pair.Value.Summary).OrderBy(summary => summary.Id, StringComparer.Ordinal).ToArray(), issues);
    }

    private (Dictionary<string, Skill> Skills, IReadOnlyList<SkillDiagnostic> Issues) Scan(ToolContext? context)
    {
        var skills = new Dictionary<string, Skill>(StringComparer.Ordinal);
        var issues = new List<SkillDiagnostic>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var visited = 0;
        foreach (var source in _sources)
        {
            if (!Directory.Exists(source.Directory) || source.Scope is not null && context is not null && new PathGuard(source.Scope).Resolve(context.Workspace).Outside) continue;
            if (source.Scope is not null && new PathGuard(source.Scope).Resolve(source.Directory).Outside)
            {
                issues.Add(new(source.Key, ".", "source-unreadable", "Project skill source resolves outside its owning scope."));
                continue;
            }
            var guard = new PathGuard(source.Directory, context?.ProtectedGlobs);
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((source.Directory, 0));
            while (pending.TryDequeue(out var directory))
            {
                if (++visited > 512) throw new IOException("Skill discovery exceeds its 512 directory budget.");
                try
                {
                    if (guard.Resolve(directory.Path) is { Outside: true } or { Protected: true } || Linked(directory.Path)) continue;
                    var entry = Path.Combine(directory.Path, "SKILL.md");
                    if (File.Exists(entry)) { Add(entry, directory.Path, new DirectoryInfo(directory.Path).Name); continue; }
                    var children = Directory.EnumerateFileSystemEntries(directory.Path).Take(2049).ToArray();
                    if (children.Length > 2048) throw new IOException("Skill source directory exceeds its 2048 entry budget.");
                    foreach (var child in children.Order(StringComparer.Ordinal))
                    {
                        if (Path.GetFileName(child).StartsWith('.') || Path.GetFileName(child) is "node_modules" or "bin" or "obj" || Linked(child)) continue;
                        if (Directory.Exists(child)) { if (directory.Depth < 4) pending.Enqueue((child, directory.Depth + 1)); }
                        else if (directory.Depth == 0 && Path.GetExtension(child) == ".md") Add(child, source.Directory, Path.GetFileNameWithoutExtension(child));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PathRejectedException)
                {
                    issues.Add(new(source.Key, Path.GetRelativePath(source.Directory, directory.Path).Replace('\\', '/'), "source-unreadable", "Source could not be inspected within its path and byte bounds."));
                }
            }

            void Add(string file, string bundle, string fallback)
            {
                if (!seen.Add(file) || guard.Resolve(file) is { Outside: true } or { Protected: true } || Linked(file)) return;
                try
                {
                    var parsed = SkillDocument.Parse(ReadHeader(file), fallback, source.Key);
                    var qualified = source.Key + "/" + parsed.Summary.Id;
                    if (skills.Count >= 512) throw new IOException("Skill discovery exceeds its 256 skill budget.");
                    var skill = new Skill(file, bundle, fallback, parsed.Summary);
                    if (!skills.TryAdd(qualified, skill with { Summary = parsed.Summary with { Id = qualified } }))
                        issues.Add(new(source.Key, Path.GetRelativePath(source.Directory, file).Replace('\\', '/'), "duplicate-id", "Same-source skill id is ambiguous; use distinct names."));
                    if (!skills.TryAdd(parsed.Summary.Id, skill))
                        issues.Add(new(source.Key, Path.GetRelativePath(source.Directory, file).Replace('\\', '/'), "shadowed-id", "Earlier source owns the short id; use the origin-qualified id."));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    issues.Add(new(source.Key, Path.GetRelativePath(source.Directory, file).Replace('\\', '/'), "invalid-skill", "Skill metadata is invalid or unsupported. " + exception.Message));
                }
            }
        }
        return (skills, issues);
    }

    private bool Allowed(SkillSummary summary, ToolContext? context, SkillCaller caller) => SkillRendering.Admitted(summary, caller) && _admission?.Invoke(summary, context, caller) != false;

    public Task<SkillContent?> LoadAsync(string id, ToolContext context, CancellationToken cancellationToken) => LoadCoreAsync(id, SkillCaller.Agent, context, cancellationToken);

    public async Task<SkillContent?> InvokeAsync(string id, SkillInvocation invocation, ToolContext context, CancellationToken cancellationToken)
    {
        var content = await LoadCoreAsync(id, invocation.Caller, context, cancellationToken);
        if (content is null) return null;
        if (invocation.Caller == SkillCaller.Agent && content.Summary!.Focus.Count > 0 &&
            !(invocation.Files ?? []).Any(path => !Path.IsPathRooted(path) && !path.Replace('\\', '/').Split('/').Contains("..") && new GlobMatcher(content.Summary.Focus).IsMatch(path)))
            throw new UnauthorizedAccessException("Skill focus does not match the explicitly supplied workspace files.");
        return SkillRendering.Expand(content, invocation, context);
    }

    private async Task<SkillContent?> LoadCoreAsync(string id, SkillCaller caller, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Scan(context).Skills.TryGetValue(id, out var skill)) return null;
        if (!Allowed(skill.Summary, context, caller)) throw new UnauthorizedAccessException("Skill is not available for this caller under current policy.");
        var guard = new PathGuard(skill.Directory, context.ProtectedGlobs);
        var file = guard.Resolve(skill.File);
        if (file.Outside || file.Protected || Linked(file.Full)) throw new IOException("Skill file escapes its registered directory or is protected.");
        var limit = Math.Min(context.Limits.MaxFileBytes, 1_048_576);
        await using var stream = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new IOException("Skill exceeds its file budget.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (text.Contains('\0')) throw new IOException("Skill body is not text.");
        if (Encoding.UTF8.GetByteCount(text) > limit) throw new IOException("Skill exceeds its file budget.");
        var parsed = SkillDocument.Parse(text, skill.Fallback, skill.Summary.Origin);
        if (parsed.Summary.Id != skill.Summary.Id.Split('/').Last()) throw new IOException("Skill identity changed while loading.");
        var summary = parsed.Summary with { Id = id };
        if (!Allowed(summary, context, caller)) throw new UnauthorizedAccessException("Skill audience or policy changed while loading.");
        var files = new List<string>();
        var pending = new Queue<string>();
        if (Path.GetFileName(skill.File) == "SKILL.md") pending.Enqueue(skill.Directory);
        var visited = 0;
        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > 1000) throw new IOException("Skill bundle has too many directories.");
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Linked(child) || guard.Resolve(child) is { Outside: true } or { Protected: true } || Path.GetFileName(child).StartsWith('.')) continue;
                if (Directory.Exists(child)) pending.Enqueue(child);
                else if (child != skill.File && Path.GetFileName(skill.File) == "SKILL.md")
                {
                    if (files.Count >= 100) throw new IOException("Skill bundle has too many files.");
                    files.Add(Path.GetRelativePath(skill.Directory, child).Replace('\\', '/'));
                }
            }
        }
        return new(id, parsed.Body, files.Order(StringComparer.Ordinal).ToArray())
        {
            Summary = summary, Parameters = parsed.Parameters, Route = parsed.Route, Model = parsed.Model, Gather = parsed.Gather, BaseDirectory = skill.Directory,
            Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
        };
    }

    public async Task<string?> ReadFileAsync(string id, string path, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Scan(context).Skills.TryGetValue(id, out var skill)) return null;
        if (!Allowed(skill.Summary, context, SkillCaller.Agent)) throw new UnauthorizedAccessException("Skill resources are not available for agent invocation.");
        if (Path.GetFileName(skill.File) != "SKILL.md") throw new UnauthorizedAccessException("Flat skills have no private resource bundle.");
        if (Path.IsPathRooted(path)) throw new UnauthorizedAccessException("Skill resources must use bundle-relative paths.");
        var guard = new PathGuard(skill.Directory, context.ProtectedGlobs);
        var resolved = guard.Resolve(path);
        if (resolved.Outside || resolved.Protected) throw new UnauthorizedAccessException("Skill resource path is outside the bundle or protected.");
        if (!File.Exists(resolved.Full)) return null;
        if ((File.GetAttributes(resolved.Full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked skill resources are not supported.");
        var limit = Math.Min(context.Limits.MaxFileBytes, 1_048_576);
        await using var stream = new FileStream(resolved.Full, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > limit) throw new IOException("Skill resource exceeds its byte budget.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1) throw new IOException("Skill resource grew while reading.");
        if (bytes.Contains((byte)0)) throw new IOException("Skill resource is not text.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static bool Linked(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1_048_576) throw new IOException("Skill exceeds its file budget.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var prefix = new char[4];
        if (reader.ReadBlock(prefix, 0, prefix.Length) != prefix.Length) return "";
        var first = new string(prefix);
        if (first != "---\n" && (first != "---\r" || reader.Read() != '\n')) return "";
        var header = new StringBuilder("---\n");
        var line = new StringBuilder();
        while (reader.Read() is var character && character >= 0)
        {
            if (header.Length + line.Length >= 32_768) throw new IOException("Skill metadata exceeds 32 KiB.");
            if (character != '\n') { line.Append((char)character); continue; }
            var value = line.ToString().TrimEnd('\r');
            header.Append(value).Append('\n');
            if (value == "---") return header.ToString();
            line.Clear();
        }
        if (line.ToString() == "---") return header.Append("---").ToString();
        throw new IOException("Skill YAML frontmatter is not terminated.");
    }
}