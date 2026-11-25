using System.Text;
using System.Text.RegularExpressions;
using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

/// <summary>One tool for both "which files" (glob) and "where is this text" (literal or regex search).</summary>
public sealed class FsFindTool(IFileSystem fileSystem) : ITool
{
    private const int PreviewChars = 240;

    private const string Schema = """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Directory (or file) to search. Defaults to the workspace root."},
          "glob":{"type":"string","description":"Only files whose path below 'path' matches, for example **/*.cs or *.json."},
          "text":{"type":"string","description":"Search file contents for this text. Without it, matching file paths are listed."},
          "regex":{"type":"boolean","description":"Treat 'text' as a regular expression."},
          "caseSensitive":{"type":"boolean"},
          "hidden":{"type":"boolean","description":"Include dot-files and dot-directories."},
          "includeIgnored":{"type":"boolean","description":"Search paths excluded by workspace.ignore."},
          "limit":{"type":"integer","minimum":1,"description":"Maximum results."}},
         "additionalProperties":false}
        """;

    public ToolSpec Spec { get; } = new(
        "fs.find",
        "Find files by glob, or search file contents by text or regex. Content results look like path:line: preview. Results are capped; narrow with path or glob when a cap is reported.",
        Schema, ToolRisk.Read, ["read"]);

    private sealed record Query(ResolvedPath Root, string? Glob, string? Text, bool Regex, bool CaseSensitive, bool Hidden, bool IncludeIgnored, int Limit);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = Parse(arguments, context);
        return new(new ToolPlan([ToolSupport.Effect(ToolEffectKind.Read, query.Root)]));
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = Parse(arguments, context);
        var facts = fileSystem.Stat(query.Root.Full);
        if (facts is null) return ToolResult.Error($"Not found: {ToolSupport.Describe(query.Root)}");

        Regex? pattern = null;
        if (query.Text is not null)
        {
            var source = query.Regex ? query.Text : Regex.Escape(query.Text);
            try
            {
                pattern = new Regex(source, RegexOptions.CultureInvariant | (query.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException exception)
            {
                throw new ToolArgumentException($"'text' is not a valid regular expression: {exception.Message}");
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.SearchTimeout);

        var guard = new PathGuard(context.Workspace);
        var ignore = new GlobMatcher(query.IncludeIgnored ? [] : context.IgnoreGlobs);
        var glob = query.Glob is null ? null : new GlobMatcher([query.Glob]);
        var notes = new List<string>();

        string Workspace(string full) => guard.IsInside(full) ? Path.GetRelativePath(guard.Root, full).Replace('\\', '/') : full;
        bool Visible(string full, bool directory)
        {
            var name = Path.GetFileName(full);
            if (!query.Hidden && name.StartsWith('.')) return false;
            if (!guard.IsInside(full)) return true;
            var relative = Workspace(full);
            return !(directory ? ignore.ExcludesDirectory(relative) : ignore.IsMatch(relative));
        }

        var files = new List<string>();
        var scanLimited = false;
        if (!facts.IsDirectory) files.Add(query.Root.Full);
        else
        {
            try
            {
                foreach (var file in fileSystem.EnumerateFiles(query.Root.Full, directory => Visible(directory, true)))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!Visible(file, false)) continue;
                    if (glob is not null && !glob.IsMatch(Path.GetRelativePath(query.Root.Full, file).Replace('\\', '/'))) continue;
                    if (files.Count >= context.Limits.MaxSearchFiles) { scanLimited = true; break; }
                    files.Add(file);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                notes.Add($"[search stopped after {context.Limits.SearchTimeout.TotalSeconds:0} s]");
            }
        }
        files.Sort(StringComparer.Ordinal);

        var output = new List<string>();
        var limited = false;
        if (pattern is null)
        {
            foreach (var file in files)
            {
                if (output.Count >= query.Limit) { limited = true; break; }
                output.Add(Workspace(file));
            }
        }
        else
        {
            var skipped = 0;
            foreach (var file in files)
            {
                if (limited || deadline.IsCancellationRequested) break;
                List<(int Line, string Preview)>? matches;
                try { matches = await SearchFileAsync(file, pattern, context, query.Limit - output.Count + 1, deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { break; }
                if (matches is null) { skipped++; continue; }
                foreach (var (line, preview) in matches)
                {
                    if (output.Count >= query.Limit) { limited = true; break; }
                    output.Add($"{Workspace(file)}:{line}: {preview}");
                }
            }
            if (skipped > 0) notes.Add($"[{skipped} binary or oversized files skipped]");
            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                notes.Add($"[search stopped after {context.Limits.SearchTimeout.TotalSeconds:0} s]");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (limited) notes.Add($"[limit of {query.Limit} reached; narrow the search or raise limit]");
        if (scanLimited) notes.Add($"[only the first {context.Limits.MaxSearchFiles} files were scanned]");
        if (output.Count == 0) return ToolResult.Ok(string.Join('\n', notes.Prepend("No matches.")));
        return ToolResult.Ok(string.Join('\n', output.Concat(notes)));
    }

    private async Task<List<(int Line, string Preview)>?> SearchFileAsync(string file, Regex pattern, ToolContext context, int maxMatches, CancellationToken cancellationToken)
    {
        var facts = fileSystem.Stat(file);
        if (facts is null || facts.Length > context.Limits.MaxFileBytes) return null;

        try
        {
            await using var stream = fileSystem.OpenRead(file);
            var head = new byte[ContentSniffer.HeadBytes];
            var read = await stream.ReadAsync(head, cancellationToken);
            if (ContentSniffer.IsBinary(head.AsSpan(0, read))) return null;
            stream.Position = 0;

            var found = new List<(int, string)>();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), true, 4096, leaveOpen: true);
            var number = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                number++;
                bool hit;
                try { hit = pattern.IsMatch(line); }
                catch (RegexMatchTimeoutException) { continue; }
                if (hit) found.Add((number, line.Length > PreviewChars ? line[..PreviewChars].Trim() + "..." : line.Trim()));
                if (found.Count >= maxMatches) break;
            }
            return found;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Query Parse(ToolArguments arguments, ToolContext context)
    {
        var path = arguments.String("path") ?? ".";
        var glob = arguments.String("glob");
        var text = arguments.String("text");
        var regex = arguments.Bool("regex");
        var caseSensitive = arguments.Bool("caseSensitive");
        var hidden = arguments.Bool("hidden");
        var includeIgnored = arguments.Bool("includeIgnored");
        var limit = arguments.Int("limit", 1, context.Limits.MaxSearchResults) ?? context.Limits.MaxSearchResults;
        if (text is { Length: 0 }) arguments.Issue("'text' must not be empty.");
        arguments.Validate();
        return new Query(ToolSupport.Resolve(path, context), glob, text, regex, caseSensitive, hidden, includeIgnored, limit);
    }
}
