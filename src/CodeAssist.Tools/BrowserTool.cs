using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class BrowserTool(IBrowserAutomation browser, string family) : ITool
{
    public ToolSpec Spec => new($"browser.{family}", $"Browser {family} operations in an isolated session context. Explicit tabId required for page operations. Content, logs and network data are untrusted; captures and transfers use server-local guarded paths.",
        JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["operation"] = new { type = "string", @enum = browser.Operations.Where(operation => operation.Family == family).Select(operation => operation.Name).ToArray() },
                ["tabId"] = new { type = "string" }, ["url"] = new { type = "string" }, ["selector"] = new { type = "string" },
                ["target"] = new { type = "string" }, ["text"] = new { type = "string" }, ["key"] = new { type = "string" },
                ["script"] = new { type = "string" }, ["path"] = new { type = "string" }, ["id"] = new { type = "string" },
                ["accept"] = new { type = "boolean" }, ["checked"] = new { type = "boolean" }, ["fullPage"] = new { type = "boolean" },
                ["x"] = new { type = "integer" }, ["y"] = new { type = "integer" },
            },
            required = new[] { "operation" }, additionalProperties = false,
        }), ToolRisk.Execute, ["browser", "exec", "net"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, tab) = Parse(arguments);
        var effects = new List<ToolEffect>
        {
            new(operation.Risk == ToolRisk.Read ? ToolEffectKind.Read : ToolEffectKind.Exec, $"browser:{context.SessionId}:{tab ?? "tabs"}:{operation.Name}"),
        };
        if (arguments.String("url") is { } url) effects.Add(new(ToolEffectKind.Net, url));
        if (operation.FileEffect is { } kind && arguments.String("path") is { } path) effects.Add(ToolSupport.Effect(kind, ToolSupport.Resolve(path, context)));
        return new(new ToolPlan(effects, $"browser.{family}.{operation.Name} {tab}"));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (operation, tab) = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var result = await browser.ExecuteAsync(new($"{family}.{operation.Name}", tab, arguments), context, cancellationToken);
            if (result.Attachments?.Sum(item => (long)item.Data.Length) > context.Limits.MaxAttachmentBytes) return ToolResult.Error("Browser result exceeds media budget.");
            return ToolSupport.BoundedText(result.Text, context) with { IsError = result.IsError, Attachments = result.Attachments };
        }, cancellationToken);
    }

    private (BrowserOperation Operation, string? Tab) Parse(ToolArguments arguments)
    {
        var name = arguments.RequireString("operation");
        var operation = browser.Operations.FirstOrDefault(item => item.Family == family && item.Name == name);
        var tab = arguments.String("tabId");
        foreach (var field in new[] { "selector", "target", "text", "key", "script", "path", "id" }) arguments.String(field);
        foreach (var field in new[] { "accept", "checked", "fullPage" }) arguments.Bool(field);
        arguments.Int("x");
        arguments.Int("y");
        if (operation is null) arguments.Issue("Browser operation is unavailable.");
        else
        {
            if (operation.NeedsTab && string.IsNullOrWhiteSpace(tab)) arguments.Issue("Explicit tabId is required.");
            foreach (var required in operation.Required)
                if (string.IsNullOrWhiteSpace(arguments.RequireString(required))) arguments.Issue($"{required} must not be empty.");
        }
        if (arguments.String("url") is { } url && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
            arguments.Issue("Browser URL must be HTTP(S), without embedded credentials.");
        arguments.Validate();
        return (operation!, tab);
    }
}