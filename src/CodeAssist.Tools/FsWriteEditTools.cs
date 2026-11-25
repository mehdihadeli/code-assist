using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class FsWriteTool(IFileSystem fileSystem) : ITool
{
    private const string Schema = """
        {"type":"object","properties":{
          "path":{"type":"string"},
          "content":{"type":"string","description":"Complete new content of the file."},
          "expectHash":{"type":"string","description":"sha256 from an earlier result; the write fails if the existing file no longer matches."}},
         "required":["path","content"],"additionalProperties":false}
        """;

    public ToolSpec Spec { get; } = new(
        "fs.write",
        "Create a file or replace its whole content. Missing parent directories are created. Prefer fs.edit for a change inside an existing file.",
        Schema, ToolRisk.Write, ["write"]);

    private sealed record Request(ResolvedPath Path, string Content, string? ExpectHash);

    public async ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments, context);
        var (existing, _) = await TextFiles.ReadAsync(fileSystem, request.Path, context.Limits, cancellationToken);
        var preview = UnifiedDiff.Create(request.Path.Relative, existing?.Text, request.Content);
        return new ToolPlan([ToolSupport.Effect(ToolEffectKind.Write, request.Path)], preview.Length == 0 ? "No changes." : preview);
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments, context);
        var shown = ToolSupport.Describe(request.Path);
        if (TextFiles.CheckSize(request.Content, context.Limits, shown) is { } tooLarge) return ToolResult.Error(tooLarge);

        var facts = fileSystem.Stat(request.Path.Full);
        if (facts?.IsDirectory == true) return ToolResult.Error($"{shown} is a directory.");

        TextFile? existing = null;
        if (facts is not null)
        {
            var (file, error) = await TextFiles.ReadAsync(fileSystem, request.Path, context.Limits, cancellationToken);
            if (file is null) return ToolResult.Error(error!);
            if (TextFiles.CheckHash(request.ExpectHash, file, shown) is { } changed) return ToolResult.Error(changed);
            existing = file;
        }
        else if (request.ExpectHash is not null)
        {
            return ToolResult.Error($"{shown} does not exist, so expectHash cannot match.");
        }

        var bytes = TextFiles.Encode(request.Content, existing?.Bom ?? false);
        await fileSystem.WriteAtomicAsync(request.Path.Full, bytes, cancellationToken);

        var summary = UnifiedDiff.Count(existing?.Text, request.Content);
        var verb = existing is null ? "Created" : "Replaced";
        return ToolResult.Ok($"{verb} {shown} (+{summary.Added} -{summary.Removed} lines, {bytes.Length:N0} bytes). sha256: {TextFiles.Hash(bytes)}");
    }

    private static Request Parse(ToolArguments arguments, ToolContext context)
    {
        var path = arguments.RequireString("path");
        var content = arguments.RequireString("content");
        var expectHash = arguments.String("expectHash");
        arguments.Validate();
        return new Request(ToolSupport.Resolve(path, context), content, expectHash);
    }
}

public sealed class FsEditTool(IFileSystem fileSystem) : ITool
{
    private const string Schema = """
        {"type":"object","properties":{
          "path":{"type":"string"},
          "oldString":{"type":"string","description":"Exact text to replace. Must match once unless replaceAll is true; include enough context to be unique."},
          "newString":{"type":"string","description":"Replacement text; must differ from oldString."},
          "replaceAll":{"type":"boolean"},
          "tolerant":{"type":"boolean","description":"Also match when only trailing spaces, curly quotes, dashes or non-breaking spaces differ."},
          "expectHash":{"type":"string","description":"sha256 from an earlier result; the edit fails if the file changed since."}},
         "required":["path","oldString","newString"],"additionalProperties":false}
        """;

    public ToolSpec Spec { get; } = new(
        "fs.edit",
        "Replace text in an existing file. oldString must match exactly once (or set replaceAll). CRLF files accept LF-written multi-line text. Read the file first.",
        Schema, ToolRisk.Write, ["write"]);

    private sealed record Request(ResolvedPath Path, string Old, string New, bool All, bool Tolerant, string? ExpectHash);

    public async ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments, context);
        var effects = new[] { ToolSupport.Effect(ToolEffectKind.Write, request.Path) };
        var (file, error) = await TextFiles.ReadAsync(fileSystem, request.Path, context.Limits, cancellationToken);
        if (file is null) return new ToolPlan(effects, $"Cannot edit: {error}");

        var outcome = TextEdit.Replace(file.Text, request.Old, request.New, request.All, request.Tolerant);
        return new ToolPlan(effects, outcome.Ok ? UnifiedDiff.Create(request.Path.Relative, file.Text, outcome.Content) : $"Cannot edit: {Explain(outcome, request.Path)}");
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments, context);
        var shown = ToolSupport.Describe(request.Path);
        var (file, error) = await TextFiles.ReadAsync(fileSystem, request.Path, context.Limits, cancellationToken);
        if (file is null) return ToolResult.Error(error!);
        if (TextFiles.CheckHash(request.ExpectHash, file, shown) is { } changed) return ToolResult.Error(changed);

        var outcome = TextEdit.Replace(file.Text, request.Old, request.New, request.All, request.Tolerant);
        if (!outcome.Ok) return ToolResult.Error(Explain(outcome, request.Path));
        if (TextFiles.CheckSize(outcome.Content, context.Limits, shown) is { } tooLarge) return ToolResult.Error(tooLarge);

        var bytes = TextFiles.Encode(outcome.Content, file.Bom);
        await fileSystem.WriteAtomicAsync(request.Path.Full, bytes, cancellationToken);
        var summary = UnifiedDiff.Count(file.Text, outcome.Content);
        return ToolResult.Ok($"Edited {shown}: {outcome.Replacements} replacement(s), +{summary.Added} -{summary.Removed} lines. sha256: {TextFiles.Hash(bytes)}");
    }

    internal static string Explain(EditOutcome outcome, ResolvedPath path) => outcome.Failure switch
    {
        EditFailure.EmptyOldText => "oldString must not be empty. To create or replace a whole file use fs.write.",
        EditFailure.Ambiguous => $"oldString matches {outcome.Matches} places in {ToolSupport.Describe(path)}. Add surrounding lines to make it unique, or set replaceAll.",
        _ => $"oldString was not found in {ToolSupport.Describe(path)}. Read the file again, or set tolerant to ignore quote, dash and trailing-space differences.",
    };

    private static Request Parse(ToolArguments arguments, ToolContext context)
    {
        var path = arguments.RequireString("path");
        var oldText = arguments.RequireString("oldString");
        var newText = arguments.RequireString("newString");
        var all = arguments.Bool("replaceAll");
        var tolerant = arguments.Bool("tolerant");
        var expectHash = arguments.String("expectHash");
        if (oldText == newText && oldText.Length > 0) arguments.Issue("'newString' must differ from 'oldString'.");
        arguments.Validate();
        return new Request(ToolSupport.Resolve(path, context), oldText, newText, all, tolerant, expectHash);
    }
}
