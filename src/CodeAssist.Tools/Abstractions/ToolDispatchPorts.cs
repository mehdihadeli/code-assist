namespace CodeAssist.Tools;

public sealed record ToolAuthorization(bool Allowed, string? Reason = null);

public interface IToolAuthorization
{
    Task<ToolAuthorization> AuthorizeAsync(ToolSpec tool, ToolPlan plan, ToolContext context, CancellationToken cancellationToken);
}

public interface IToolDispatcher
{
    ToolSpec? Describe(string name, ToolContext context);
    Task<ToolResult> InvokeAsync(string name, string arguments, ToolContext context, CancellationToken cancellationToken);
}