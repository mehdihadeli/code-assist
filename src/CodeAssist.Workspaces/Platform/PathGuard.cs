namespace CodeAssist.Workspaces.Platform;

/// <param name="Full">Absolute, normalized path.</param>
/// <param name="Relative">Path relative to the workspace using '/', or the full path when outside.</param>
/// <param name="Outside">The path (after following links) is not inside the workspace.</param>
/// <param name="Protected">The path matches a protected pattern such as <c>.env*</c>.</param>
public sealed record ResolvedPath(string Full, string Relative, bool Outside, bool Protected);

public sealed class PathRejectedException(string message) : Exception(message);

/// <summary>
/// The one way tools turn model-supplied text into a file path. It rejects device paths, alternate data streams and
/// control characters, resolves relative paths against the workspace, follows symlinks/junctions of existing ancestors,
/// and reports whether the result escapes the workspace so policy can ask.
/// </summary>
public sealed class PathGuard
{
    private readonly string _root;
    private readonly string _realRoot;
    private readonly GlobMatcher _protected;
    private readonly StringComparison _comparison;

    public PathGuard(string workspaceRoot, IEnumerable<string>? protectedGlobs = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _realRoot = ResolveLinks(_root);
        _protected = new GlobMatcher(protectedGlobs ?? []);
        _comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public string Root => _root;

    public ResolvedPath Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new PathRejectedException("The path is empty.");
        if (path.Any(char.IsControl)) throw new PathRejectedException("The path contains control characters.");
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new PathRejectedException("Device paths are not allowed.");
        if (OperatingSystem.IsWindows() && HasAlternateStream(path))
            throw new PathRejectedException("Alternate data streams are not allowed.");

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, _root));
        var real = ResolveLinks(full);
        var outside = !IsInside(full, _root) || !IsInside(real, _realRoot);

        var relative = outside ? full : Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (relative == ".") relative = "";
        var isProtected = !outside && relative.Length > 0 && _protected.IsMatch(relative);
        return new ResolvedPath(full, relative.Length == 0 ? "." : relative, outside, isProtected);
    }

    public bool IsInside(string fullPath) => IsInside(fullPath, _root);

    private bool IsInside(string fullPath, string root) =>
        fullPath.Equals(root, _comparison) ||
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, _comparison) ||
        fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, _comparison);

    private static bool HasAlternateStream(string path)
    {
        var rest = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' ? path[2..] : path;
        return rest.Contains(':');
    }

    /// <summary>Follows links on the deepest existing ancestors so a symlink inside the workspace cannot lead out of it.</summary>
    private static string ResolveLinks(string full)
    {
        var existing = full;
        var suffix = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) return full;
            suffix.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = existing;
        try
        {
            FileSystemInfo info = Directory.Exists(existing) ? new DirectoryInfo(existing) : new FileInfo(existing);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) resolved = target.FullName;
            else resolved = ResolveAncestors(existing);
        }
        catch (IOException) { }
        foreach (var segment in suffix) resolved = Path.Combine(resolved, segment);
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static string ResolveAncestors(string path)
    {
        var current = path;
        while (Path.GetDirectoryName(current) is { } parent)
        {
            var info = new DirectoryInfo(parent);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target)
                return Path.Combine(target.FullName, Path.GetRelativePath(parent, path));
            current = parent;
        }
        return path;
    }
}
