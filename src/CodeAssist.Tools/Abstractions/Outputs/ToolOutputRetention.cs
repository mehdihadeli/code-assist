using CodeAssist.Tools.Text;

namespace CodeAssist.Tools;

public static class ToolOutputRetention
{
    public static async Task<ToolResult> CompleteAsync(ToolResult result, ToolContext context, CancellationToken cancellationToken)
    {
        var preview = OutputBudget.Head(result.Text, context.Limits.OutputMaxLines, context.Limits.OutputMaxBytes);
        if (!preview.Truncated) return result;
        if (context.Outputs is null) return result with { Text = preview.Text + $"\n[output truncated; truncated: {preview.OmittedLines:N0} more lines omitted; retention unavailable]" };
        var id = await context.Outputs.SaveAsync(context.SessionId, result.Text, cancellationToken);
        return result with { Text = $"[Retained output: {id}; use output.read]\n" + preview.Text, OutputId = id };
    }
}