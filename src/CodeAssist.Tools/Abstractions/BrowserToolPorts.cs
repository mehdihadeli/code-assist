using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed record BrowserOperation(string Family, string Name, ToolRisk Risk, IReadOnlyList<string> Required, bool NeedsTab = true, ToolEffectKind? FileEffect = null);
public sealed record BrowserCall(string Operation, string? TabId, ToolArguments Arguments);

public interface IBrowserAutomation
{
    IReadOnlyList<BrowserOperation> Operations { get; }
    Task<ToolResult> ExecuteAsync(BrowserCall call, ToolContext context, CancellationToken cancellationToken);
}