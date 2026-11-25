using CodeAssist.Persistence;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Extensions.Plugins;

public sealed class PluginToolDispatcher(IToolDispatcher inner, PluginCatalog plugins, IJournalStore? journal = null) : IToolDispatcher
{
    public ToolSpec? Describe(string name, ToolContext context) => inner.Describe(name, context);

    public async Task<ToolResult> InvokeAsync(string name, string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var link = journal is null ? null : (await journal.ReadAsync(context.SessionId, cancellationToken)).OfType<DelegationLinked>().SingleOrDefault();
        if (link is not null && (link.AllowedTools is null || link.Role != context.Role))
            return ToolResult.Error("Delegated tool execution requires its original role and permission snapshot.");
        var effective = link is null ? inner : new AncestryDispatcher(inner, link.AllowedTools!);
        if (effective.Describe(name, context) is null) return ToolResult.Error("Tool is not available in this session.");
        foreach (var hook in plugins.Hooks("tool.before", context.Workspace))
        {
            var results = await plugins.ExecuteAsync(hook, effective, context, cancellationToken);
            if (results.Any(result => result.IsError)) return ToolResult.Error("Plugin pre-tool check did not pass; requested tool was not executed.");
        }
        var result = await effective.InvokeAsync(name, arguments, context, cancellationToken);
        foreach (var hook in plugins.Hooks("tool.after", context.Workspace))
        {
            try
            {
                var results = await plugins.ExecuteAsync(hook, effective, context, cancellationToken);
                if (results.Any(item => item.IsError)) result = result with { Text = result.Text + "\nPlugin post-tool check failed; the preceding tool result remains committed." };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = result with { Text = result.Text + "\nPlugin post-tool check was unavailable; the preceding tool result remains committed." };
            }
        }
        return result;
    }

    private sealed class AncestryDispatcher(IToolDispatcher dispatcher, IReadOnlyList<string> allowed) : IToolDispatcher
    {
        public ToolSpec? Describe(string name, ToolContext context) => allowed.Contains(name, StringComparer.Ordinal) ? dispatcher.Describe(name, context) : null;

        public Task<ToolResult> InvokeAsync(string name, string arguments, ToolContext context, CancellationToken cancellationToken) =>
            Describe(name, context) is null ? Task.FromResult(ToolResult.Error("Tool is outside the delegated permission snapshot.")) : dispatcher.InvokeAsync(name, arguments, context, cancellationToken);
    }
}