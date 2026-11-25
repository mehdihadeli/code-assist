using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class NotebookEditTool(IFileSystem fileSystem) : ITool
{
    public ToolSpec Spec { get; } = new(
        "notebook.edit", "Insert, replace or remove one cell in an existing v4 Jupyter notebook. Indices are zero-based. Metadata and existing cell IDs are preserved; edited code outputs are cleared. Supports dryRun and expectHash.",
        """
        {"type":"object","properties":{
          "path":{"type":"string"},"operation":{"type":"string","enum":["insert","replace","remove"]},
          "index":{"type":"integer","minimum":0},"source":{"type":"string"},
          "kind":{"type":"string","enum":["code","markdown","raw"]},
          "expectHash":{"type":"string"},"dryRun":{"type":"boolean"}},
         "required":["path","operation","index"],"additionalProperties":false}
        """, ToolRisk.Write, ["write"]);

    private sealed record Request(ResolvedPath Path, string Operation, int Index, string? Source, string? Kind, string? Hash, bool DryRun);

    public async ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments, context);
        var (_, updated, preview) = await PrepareAsync(request, context, cancellationToken);
        return new ToolPlan([ToolSupport.Effect(request.DryRun ? ToolEffectKind.Read : ToolEffectKind.Write, request.Path)],
            updated is null ? preview : ToolSupport.BoundedText(preview, context).Text);
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments, context);
        return ToolSupport.FromPortAsync(async () =>
        {
            var (original, updated, preview) = await PrepareAsync(request, context, cancellationToken);
            if (updated is null) return ToolResult.Error(preview);
            if (request.DryRun) return ToolSupport.BoundedText("Preview only; no cells were changed.\n" + preview, context);
            var bytes = TextFiles.Encode(updated, original!.Bom);
            await fileSystem.WriteAtomicAsync(request.Path.Full, bytes, cancellationToken);
            return ToolSupport.BoundedText($"Notebook {request.Operation} at index {request.Index}. sha256: {TextFiles.Hash(bytes)}\n{preview}", context);
        }, cancellationToken);
    }

    private async Task<(TextFile? Original, string? Updated, string Preview)> PrepareAsync(Request request, ToolContext context, CancellationToken cancellationToken)
    {
        var (original, error) = await TextFiles.ReadAsync(fileSystem, request.Path, context.Limits, cancellationToken);
        if (original is null) return (null, null, error!);
        if (TextFiles.CheckHash(request.Hash, original, ToolSupport.Describe(request.Path)) is { } changed) return (original, null, changed);
        JsonObject? document;
        try { document = JsonNode.Parse(original.Text) as JsonObject; }
        catch (JsonException) { return (original, null, "Notebook is not valid JSON."); }
        if (document?["nbformat"] is not JsonValue version || !version.TryGetValue<int>(out var major) || major != 4 || document["cells"] is not JsonArray cells)
            return (original, null, "Expected a v4 notebook with a cells array.");
        if (request.Index > cells.Count || request.Operation != "insert" && request.Index == cells.Count)
            return (original, null, "Cell index is outside the notebook.");

        if (request.Operation == "remove") cells.RemoveAt(request.Index);
        else
        {
            var cell = request.Operation == "insert" ? new JsonObject { ["metadata"] = new JsonObject() } : cells[request.Index] as JsonObject;
            if (cell is null) return (original, null, "The selected cell must be an object.");
            var kind = request.Kind ?? cell["cell_type"]?.GetValue<string>() ?? "code";
            if (kind is not ("code" or "markdown" or "raw")) return (original, null, "The selected cell has an unsupported kind.");
            cell["cell_type"] = kind;
            cell["source"] = request.Source;
            if (kind == "code")
            {
                cell["outputs"] = new JsonArray();
                cell["execution_count"] = null;
            }
            else
            {
                cell.Remove("outputs");
                cell.Remove("execution_count");
            }
            if (request.Operation == "insert")
            {
                if (document["nbformat_minor"] is JsonValue minor && minor.TryGetValue<int>(out var revision) && revision >= 5)
                    cell["id"] = Guid.NewGuid().ToString("N");
                cells.Insert(request.Index, cell);
            }
        }

        var updated = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (TextFiles.CheckSize(updated, context.Limits, "Notebook") is { } tooLarge) return (original, null, tooLarge);
        cancellationToken.ThrowIfCancellationRequested();
        var diff = UnifiedDiff.Create(ToolSupport.Describe(request.Path), original.Text, updated, maxLines: context.Limits.OutputMaxLines);
        return (original, updated, diff);
    }

    private static Request Parse(ToolArguments arguments, ToolContext context)
    {
        var path = arguments.RequireString("path");
        var operation = arguments.RequireString("operation");
        var index = arguments.Int("index", 0);
        var source = arguments.String("source");
        var kind = arguments.String("kind");
        var hash = arguments.String("expectHash");
        var dryRun = arguments.Bool("dryRun");
        if (!string.Equals(Path.GetExtension(path), ".ipynb", StringComparison.OrdinalIgnoreCase)) arguments.Issue("path must identify an .ipynb file.");
        if (operation is not ("insert" or "replace" or "remove")) arguments.Issue("operation must be insert, replace or remove.");
        if (index is null) arguments.Issue("index is required.");
        if (operation != "remove" && source is null) arguments.Issue("source is required for insert or replace.");
        if (operation == "remove" && (source is not null || kind is not null)) arguments.Issue("remove does not accept source or kind.");
        if (kind is not null and not ("code" or "markdown" or "raw")) arguments.Issue("kind must be code, markdown or raw.");
        arguments.Validate();
        return new(ToolSupport.Resolve(path, context), operation, index!.Value, source, kind, hash, dryRun);
    }
}