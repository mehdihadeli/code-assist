using CodeAssist.Tools.Text;
using CodeAssist.Tools;

namespace CodeAssist.Tools.Execution;

public sealed class ToolDispatcher(Func<ToolContext, ToolCatalog> visibleCatalog, IToolAuthorization authorization) : IToolDispatcher
{
    private readonly SemaphoreSlim _mutations = new(1, 1);

    public ToolSpec? Describe(string name, ToolContext context) => visibleCatalog(context).Find(name)?.Spec;

    public async Task<ToolResult> InvokeAsync(string name, string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = visibleCatalog(context);
        var tool = catalog.Find(name);
        if (tool is null) return ToolResult.Error($"Tool unavailable: {name}");
        var serial = !tool.Spec.ReadOnly && !tool.Spec.Tags.Contains("flow", StringComparer.OrdinalIgnoreCase);
        if (serial) await _mutations.WaitAsync(cancellationToken);
        try
        {
            var input = ToolArguments.Parse(arguments);
            catalog.ValidateArguments(tool, input);
            var plan = await tool.PlanAsync(input, context, cancellationToken);
            var decision = await authorization.AuthorizeAsync(tool.Spec, plan, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!decision.Allowed) return ToolResult.Error($"Tool denied: {name}. {decision.Reason}");
            var result = await tool.ExecuteAsync(input, context, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Attachments?.Sum(attachment => (long)attachment.Data.Length) > context.Limits.MaxAttachmentBytes)
                return ToolResult.Error("Tool result exceeds aggregate attachment budget.");
            var output = OutputBudget.Head(result.Text, context.Limits.OutputMaxLines, context.Limits.OutputMaxBytes);
            return result with { Text = output.Truncated ? output.Text + "\n[output truncated]" : output.Text };
        }
        catch (ToolArgumentException exception) { return ToolResult.Error(exception.Message); }
        finally { if (serial) _mutations.Release(); }
    }
}