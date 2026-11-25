using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class FsDiffTool(IFileSystem fileSystem) : ITool
{
    public ToolSpec Spec { get; } = new(
        "fs.diff", "Compare a UTF-8 file with proposed content or another file without writing. Changed-content previews include the source SHA-256 for guarded edits; contextLines controls hunk detail.",
        """
        {"type":"object","properties":{
          "path":{"type":"string"},"content":{"type":"string"},"otherPath":{"type":"string"},
          "contextLines":{"type":"integer","minimum":0,"maximum":20}},
         "required":["path"],"oneOf":[{"required":["content"]},{"required":["otherPath"]}],"additionalProperties":false}
        """, ToolRisk.Read, ["read"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (path, other, _, _) = Parse(arguments, context);
        var effects = new List<ToolEffect> { ToolSupport.Effect(ToolEffectKind.Read, path) };
        if (other is not null) effects.Add(ToolSupport.Effect(ToolEffectKind.Read, other));
        return new(new ToolPlan(effects));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (path, other, content, contextLines) = Parse(arguments, context);
        return ToolSupport.FromPortAsync(async () =>
        {
            var (original, error) = await TextFiles.ReadAsync(fileSystem, path, context.Limits, cancellationToken);
            if (error is not null) return ToolResult.Error(error);
            if (other is not null)
            {
                var (comparison, comparisonError) = await TextFiles.ReadAsync(fileSystem, other, context.Limits, cancellationToken);
                if (comparisonError is not null) return ToolResult.Error(comparisonError);
                content = comparison!.Text;
            }
            if (TextFiles.CheckSize(content!, context.Limits, "Comparison content") is { } sizeError)
                return ToolResult.Error(sizeError);
            cancellationToken.ThrowIfCancellationRequested();
            var diff = UnifiedDiff.Create(ToolSupport.Describe(path), original!.Text, content!, contextLines, context.Limits.OutputMaxLines);
            return ToolSupport.BoundedText(diff.Length == 0 ? "No differences." : $"Source sha256: {original.Hash}\n{diff}", context);
        }, cancellationToken);
    }

    private static (ResolvedPath Path, ResolvedPath? Other, string? Content, int ContextLines) Parse(ToolArguments arguments, ToolContext context)
    {
        var path = arguments.RequireString("path");
        var other = arguments.String("otherPath");
        var content = arguments.String("content");
        var contextLines = arguments.Int("contextLines", 0, 20) ?? 3;
        if ((other is null) == (content is null)) arguments.Issue("Supply exactly one of 'content' or 'otherPath'.");
        arguments.Validate();
        return (ToolSupport.Resolve(path, context), other is null ? null : ToolSupport.Resolve(other, context), content, contextLines);
    }
}