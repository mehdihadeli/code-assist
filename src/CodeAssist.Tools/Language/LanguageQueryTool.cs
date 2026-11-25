using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class LanguageQueryTool(ILanguageService language, IFileSystem files) : ITool
{
    public ToolSpec Spec { get; } = new("code.query", "Query the configured language server for definitions, references, hover, symbols, implementations or call hierarchy. Positions are 1-based UTF-16. Reads current file text; never executes edits or arbitrary server commands.",
        """{"type":"object","properties":{"operation":{"enum":["definition","references","hover","documentSymbols","workspaceSymbols","implementation","incomingCalls","outgoingCalls"]},"path":{"type":"string","minLength":1},"language":{"type":"string","minLength":1,"maxLength":80},"line":{"type":"integer","minimum":1},"column":{"type":"integer","minimum":1},"query":{"type":"string","maxLength":2000}},"required":["operation","path","language"],"additionalProperties":false}""",
        ToolRisk.Execute, ["code", "read", "exec"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Parse(arguments, context);
        return new(new ToolPlan([ToolSupport.Effect(ToolEffectKind.Read, path), new(ToolEffectKind.Exec, $"lsp:{language.Server}:{arguments.RequireString("operation")}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var path = Parse(arguments, context);
        return ToolSupport.FromPortAsync(async () =>
        {
            var (file, error) = await TextFiles.ReadAsync(files, path, context.Limits, cancellationToken);
            if (error is not null) return ToolResult.Error(error);
            var result = await language.QueryAsync(new(arguments.RequireString("operation"), path.Full, arguments.RequireString("language"), file!.Text,
                arguments.Int("line", 1) ?? 1, arguments.Int("column", 1) ?? 1, arguments.String("query")), context, cancellationToken);
            return ToolSupport.BoundedText(result.GetRawText(), context);
        }, cancellationToken);
    }

    private static ResolvedPath Parse(ToolArguments arguments, ToolContext context)
    {
        var operation = arguments.RequireString("operation");
        var path = arguments.RequireString("path");
        var language = arguments.RequireString("language");
        arguments.Int("line", 1);
        arguments.Int("column", 1);
        if (operation is not ("definition" or "references" or "hover" or "documentSymbols" or "workspaceSymbols" or "implementation" or "incomingCalls" or "outgoingCalls")) arguments.Issue("Unsupported language operation.");
        if (string.IsNullOrWhiteSpace(language) || language.Length > 80 || arguments.String("query")?.Length > 2000) arguments.Issue("Language or symbol query exceeds limits.");
        arguments.Validate();
        var resolved = ToolSupport.Resolve(path, context);
        if (resolved.Outside || resolved.Protected) throw new ToolArgumentException("Language queries require unprotected workspace files.");
        return resolved;
    }
}