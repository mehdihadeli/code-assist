using System.Text;
using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

/// <summary>
/// Several file changes as one all-or-nothing operation. Operations are structured (not a text patch): each names a
/// path and what to do. Every change is computed in memory first; if any fails, nothing is written. If writing fails
/// part-way, completed steps are undone.
/// </summary>
public sealed class FsPatchTool(IFileSystem fileSystem) : ITool
{
    private const string Schema = """
        {"type":"object","properties":{
                    "dryRun":{"type":"boolean","description":"Validate and show changes without writing files."},
          "operations":{"type":"array","minItems":1,"items":{"type":"object","properties":{
            "op":{"type":"string","enum":["add","update","delete","move"]},
            "path":{"type":"string"},
            "to":{"type":"string","description":"Destination for move."},
            "content":{"type":"string","description":"Full content for add."},
            "expectHash":{"type":"string","description":"Expected source SHA-256; reject stale update, move or delete."},
            "edits":{"type":"array","description":"For update, or move with changes: applied in order.","items":{"type":"object","properties":{
              "oldString":{"type":"string"},"newString":{"type":"string"},"replaceAll":{"type":"boolean"}},
              "required":["oldString","newString"]}},
            "tolerant":{"type":"boolean"}},
            "required":["op","path"]}}},
         "required":["operations"],"additionalProperties":false}
        """;

    public ToolSpec Spec { get; } = new(
        "fs.patch",
        "Apply several file changes transactionally: add, update, delete, or move. dryRun validates and previews without writing. expectHash guards source files; results include bounded change details. Completed changes are rolled back on failure or cancellation; rollback failures are reported.",
        Schema, ToolRisk.Write, ["write"]);

    private sealed record EditStep(string Old, string New, bool All);

    private sealed record Operation(int Index, string Kind, ResolvedPath Path, ResolvedPath? To, string? Content, IReadOnlyList<EditStep> Edits, bool Tolerant, string? ExpectHash);

    private sealed record Change(Operation Operation, byte[]? OldBytes, string? NewText, bool Bom, string? OldText);

    public async ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var dryRun = arguments.Bool("dryRun");
        var operations = Parse(arguments, context);
        var effects = new List<ToolEffect>();
        foreach (var op in operations)
        {
            if (dryRun)
            {
                effects.Add(ToolSupport.Effect(ToolEffectKind.Read, op.Path));
                if (op.To is not null) effects.Add(ToolSupport.Effect(ToolEffectKind.Read, op.To));
                continue;
            }
            switch (op.Kind)
            {
                case "delete": effects.Add(ToolSupport.Effect(ToolEffectKind.Delete, op.Path)); break;
                case "move":
                    effects.Add(ToolSupport.Effect(ToolEffectKind.Delete, op.Path));
                    effects.Add(ToolSupport.Effect(ToolEffectKind.Write, op.To!));
                    break;
                default: effects.Add(ToolSupport.Effect(ToolEffectKind.Write, op.Path)); break;
            }
        }

        var (changes, error) = await ComputeAsync(operations, context, cancellationToken);
        return new ToolPlan(effects, error is not null ? $"Cannot apply: {error}" : ToolSupport.BoundedText(Preview(changes!), context).Text);
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var dryRun = arguments.Bool("dryRun");
        var operations = Parse(arguments, context);
        var (changes, error) = await ComputeAsync(operations, context, cancellationToken);
        if (error is not null) return ToolResult.Error($"{error} No files were changed.");
        if (dryRun) return ToolSupport.BoundedText("Preview only; no files were changed.\n" + Preview(changes!), context);

        var undo = new List<Func<Task>>();
        var summary = new List<string>();
        try
        {
            foreach (var change in changes!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyAsync(change, undo, cancellationToken);
                summary.Add(Describe(change));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var failures = new List<string>();
            foreach (var undoStep in Enumerable.Reverse(undo))
            {
                try { await undoStep(); }
                catch (Exception undoFailure) when (undoFailure is IOException or UnauthorizedAccessException) { failures.Add(undoFailure.Message); }
            }
            if (exception is OperationCanceledException)
            {
                if (failures.Count > 0)
                    throw new OperationCanceledException($"Patch cancelled and rollback was incomplete: {string.Join("; ", failures)}.", exception, cancellationToken);
                throw;
            }
            return ToolResult.Error(failures.Count == 0
                ? $"Writing failed ({exception.Message}). All changes were rolled back."
                : $"Writing failed ({exception.Message}) and rollback was incomplete: {string.Join("; ", failures)}.");
        }

        return ToolSupport.BoundedText($"Applied {summary.Count} operation(s):\n" + string.Join('\n', summary) + "\n\n" + Preview(changes!), context);
    }

    private async Task ApplyAsync(Change change, List<Func<Task>> undo, CancellationToken cancellationToken)
    {
        var op = change.Operation;
        switch (op.Kind)
        {
            case "add":
                await fileSystem.WriteAtomicAsync(op.Path.Full, TextFiles.Encode(change.NewText!, false), cancellationToken);
                undo.Add(() => { fileSystem.DeleteFile(op.Path.Full); return Task.CompletedTask; });
                break;
            case "update":
                await fileSystem.WriteAtomicAsync(op.Path.Full, TextFiles.Encode(change.NewText!, change.Bom), cancellationToken);
                undo.Add(() => fileSystem.WriteAtomicAsync(op.Path.Full, change.OldBytes!, CancellationToken.None));
                break;
            case "delete":
                fileSystem.DeleteFile(op.Path.Full);
                undo.Add(() => fileSystem.WriteAtomicAsync(op.Path.Full, change.OldBytes!, CancellationToken.None));
                break;
            case "move" when change.NewText is null:
                fileSystem.MoveFile(op.Path.Full, op.To!.Full);
                undo.Add(() => { fileSystem.MoveFile(op.To.Full, op.Path.Full); return Task.CompletedTask; });
                break;
            case "move":
                await fileSystem.WriteAtomicAsync(op.To!.Full, TextFiles.Encode(change.NewText, change.Bom), cancellationToken);
                undo.Add(() => { fileSystem.DeleteFile(op.To.Full); return Task.CompletedTask; });
                fileSystem.DeleteFile(op.Path.Full);
                undo.Add(() => fileSystem.WriteAtomicAsync(op.Path.Full, change.OldBytes!, CancellationToken.None));
                break;
        }
    }

    /// <summary>Validates every operation against the current files and computes the result without writing.</summary>
    private async Task<(List<Change>? Changes, string? Error)> ComputeAsync(IReadOnlyList<Operation> operations, ToolContext context, CancellationToken cancellationToken)
    {
        var changes = new List<Change>();
        foreach (var op in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = $"Operation {op.Index + 1} ({op.Kind} {ToolSupport.Describe(op.Path)})";
            var facts = fileSystem.Stat(op.Path.Full);

            if (op.Kind == "add")
            {
                if (facts is not null) return (null, $"{label}: the path already exists. Use update or fs.write to change it.");
                if (TextFiles.CheckSize(op.Content!, context.Limits, ToolSupport.Describe(op.Path)) is { } big) return (null, $"{label}: {big}");
                changes.Add(new Change(op, null, op.Content, false, null));
                continue;
            }

            if (op.Kind == "delete")
            {
                if (facts is null) return (null, $"{label}: not found.");
                if (facts.IsDirectory) return (null, $"{label}: directories cannot be deleted with fs.patch.");
                if (facts.Length > context.Limits.MaxFileBytes) return (null, $"{label}: the file is over the {context.Limits.MaxFileBytes:N0} byte limit, so it cannot be restored if a later step fails.");
                var bytes = await fileSystem.ReadAllBytesAsync(op.Path.Full, cancellationToken);
                if (op.ExpectHash is not null && !string.Equals(op.ExpectHash, TextFiles.Hash(bytes), StringComparison.OrdinalIgnoreCase))
                    return (null, $"{label}: source changed since it was read; expectHash does not match.");
                string? oldText = null;
                if (!bytes.Contains((byte)0))
                {
                    try { oldText = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
                    catch (DecoderFallbackException) { }
                }
                changes.Add(new Change(op, bytes, null, false, oldText));
                continue;
            }

            var (file, error) = await TextFiles.ReadAsync(fileSystem, op.Path, context.Limits, cancellationToken);
            if (file is null) return (null, $"{label}: {error}");
            if (TextFiles.CheckHash(op.ExpectHash, file, ToolSupport.Describe(op.Path)) is { } changed) return (null, $"{label}: {changed}");

            if (op.Kind == "move" && fileSystem.Stat(op.To!.Full) is not null)
                return (null, $"{label}: the destination {ToolSupport.Describe(op.To)} already exists.");

            var text = file.Text;
            foreach (var (editNumber, step) in op.Edits.Index())
            {
                var outcome = TextEdit.Replace(text, step.Old, step.New, step.All, op.Tolerant);
                if (!outcome.Ok) return (null, $"{label}, edit {editNumber + 1}: {FsEditTool.Explain(outcome, op.Path)}");
                text = outcome.Content;
            }
            if (TextFiles.CheckSize(text, context.Limits, ToolSupport.Describe(op.Path)) is { } tooLarge) return (null, $"{label}: {tooLarge}");

            var pureMove = op.Kind == "move" && op.Edits.Count == 0;
            changes.Add(new Change(op, file.Bytes, pureMove ? null : text, file.Bom, file.Text));
        }
        return (changes, null);
    }

    private static string Preview(List<Change> changes)
    {
        var builder = new StringBuilder();
        foreach (var change in changes)
        {
            var op = change.Operation;
            switch (op.Kind)
            {
                case "add": builder.AppendLine(UnifiedDiff.Create(op.Path.Relative, null, change.NewText!)); break;
                case "update": builder.AppendLine(UnifiedDiff.Create(op.Path.Relative, change.OldText, change.NewText!)); break;
                case "delete":
                    builder.AppendLine($"delete {ToolSupport.Describe(op.Path)}");
                    if (change.OldText is not null) builder.AppendLine(UnifiedDiff.Create(op.Path.Relative, change.OldText, "", isDeletion: true));
                    break;
                default:
                    builder.AppendLine($"move {ToolSupport.Describe(op.Path)} -> {ToolSupport.Describe(op.To!)}");
                    if (change.NewText is not null) builder.AppendLine(UnifiedDiff.Create(op.To!.Relative, change.OldText, change.NewText));
                    break;
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static string Describe(Change change)
    {
        var op = change.Operation;
        return op.Kind switch
        {
            "add" => $"add {ToolSupport.Describe(op.Path)} (+{UnifiedDiff.Count(null, change.NewText!).Added} lines)",
            "update" => $"update {ToolSupport.Describe(op.Path)} (+{UnifiedDiff.Count(change.OldText, change.NewText!).Added} -{UnifiedDiff.Count(change.OldText, change.NewText!).Removed})",
            "delete" => $"delete {ToolSupport.Describe(op.Path)}",
            _ => $"move {ToolSupport.Describe(op.Path)} -> {ToolSupport.Describe(op.To!)}",
        };
    }

    private static List<Operation> Parse(ToolArguments arguments, ToolContext context)
    {
        var items = arguments.Objects("operations", required: true);
        var parsed = new List<(int Index, string Kind, string Path, string? To, string? Content, List<EditStep> Edits, bool Tolerant, string? ExpectHash)>();
        foreach (var (i, item) in items.Index())
        {
            var kind = item.RequireString("op");
            var path = item.RequireString("path");
            var to = item.String("to");
            var content = item.String("content");
            var tolerant = item.Bool("tolerant");
            var expectHash = item.String("expectHash");
            if (kind == "add" && expectHash is not null) arguments.Issue($"operations[{i}]: expectHash applies only to existing source files.");
            var edits = new List<EditStep>();
            foreach (var edit in item.Objects("edits"))
                edits.Add(new EditStep(edit.RequireString("oldString"), edit.RequireString("newString"), edit.Bool("replaceAll")));

            switch (kind)
            {
                case "add": if (content is null) arguments.Issue($"operations[{i}]: 'content' is required for add."); break;
                case "update": if (edits.Count == 0) arguments.Issue($"operations[{i}]: update needs at least one edit."); break;
                case "delete": break;
                case "move": if (string.IsNullOrWhiteSpace(to)) arguments.Issue($"operations[{i}]: 'to' is required for move."); break;
                case "": break;
                default: arguments.Issue($"operations[{i}]: op must be add, update, delete or move."); break;
            }
            parsed.Add((i, kind, path, to, content, edits, tolerant, expectHash));
        }
        arguments.Validate();

        var operations = parsed
            .Select(p => new Operation(p.Index, p.Kind, ToolSupport.Resolve(p.Path, context), p.To is null ? null : ToolSupport.Resolve(p.To, context), p.Content, p.Edits, p.Tolerant, p.ExpectHash))
            .ToList();

        var touched = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var op in operations)
            foreach (var path in new[] { op.Path.Full, op.To?.Full }.Where(p => p is not null))
                if (!touched.Add(path!)) throw new ToolArgumentException($"Path '{path}' appears in more than one operation; combine them.");
        return operations;
    }
}
