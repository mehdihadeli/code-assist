using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class ConfigManageTool(IConfigurationAccess configuration) : ITool
{
    public ToolSpec Spec { get; } = new("config.manage", "Read redacted effective configuration with origins, locks and revision. Set/unset only explicitly permitted presentation preferences in user scope. Never changes permissions, providers, environment, commands or secrets.",
        """{"type":"object","properties":{"operation":{"enum":["get","set","unset"]},"path":{"type":"string","minLength":1,"maxLength":256},"value":{},"revision":{"type":"string","minLength":1,"maxLength":256}},"required":["operation"],"additionalProperties":false}""",
        ToolRisk.Write, ["configuration", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = Parse(arguments);
        return new(new ToolPlan([new(operation == "get" ? ToolEffectKind.Read : ToolEffectKind.Write, $"configuration:{arguments.String("path") ?? "*"}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var operation = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            var result = operation == "get" ? await configuration.ReadAsync(arguments.String("path"), cancellationToken) :
                await configuration.ChangeAsync(operation, arguments.RequireString("path"), arguments.Has("value") ? arguments.Json.GetProperty("value") : null,
                    arguments.RequireString("revision"), cancellationToken);
            return ToolSupport.BoundedText(result.GetRawText(), context);
        }, cancellationToken);
    }

    private static string Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("get" or "set" or "unset")) arguments.Issue("Unknown configuration operation.");
        if (operation != "get")
        {
            arguments.RequireString("path");
            arguments.RequireString("revision");
        }
        if (operation == "set" && !arguments.Has("value")) arguments.Issue("Set requires a value.");
        if (operation != "set" && arguments.Has("value")) arguments.Issue("Only set accepts a value.");
        arguments.Validate();
        return operation;
    }
}