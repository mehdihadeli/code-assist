using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class TestFeedbackTool(ITestFeedback feedback) : ITool
{
    public ToolSpec Spec { get; } = new("test.feedback", "Run a trusted test profile, read a session-owned structured report, or find related tests by filename convention. Missing reports are not passing tests. Execution can run arbitrary project build/test code and requires authorization.",
        """{"type":"object","properties":{"operation":{"enum":["profiles","run","read","related"]},"profile":{"type":"string"},"filter":{"type":"string","maxLength":2000},"id":{"type":"string","format":"uuid"},"path":{"type":"string"}},"required":["operation"],"additionalProperties":false}""", ToolRisk.Execute, ["test", "read", "exec"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var operation = Parse(arguments);
        return new(new ToolPlan(operation == "run" ? [new(ToolEffectKind.Exec, $"test:{arguments.RequireString("profile")}"), new(ToolEffectKind.Write, $"session:{context.SessionId}:test-reports")] : [new(ToolEffectKind.Read, operation == "related" ? "workspace:test-discovery" : $"session:{context.SessionId}:test-reports")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var operation = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            object value = operation switch
            {
                "profiles" => feedback.Profiles,
                "run" => await feedback.RunAsync(arguments.RequireString("profile"), arguments.String("filter"), context, cancellationToken),
                "read" => await feedback.ReadAsync(Guid.Parse(arguments.RequireString("id")), context, cancellationToken),
                _ => new { strategy = "filename-convention", matches = await feedback.RelatedAsync(arguments.RequireString("path"), context, cancellationToken) },
            };
            var payload = JsonSerializer.SerializeToElement(value);
            return await ToolOutputRetention.CompleteAsync(new(payload.GetRawText(), value is TestRunFeedback run && run.Status != "completed", StructuredContent: payload), context, cancellationToken);
        }, cancellationToken);
    }

    private static string Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("profiles" or "run" or "read" or "related")) arguments.Issue("Unknown test operation.");
        if (operation == "run") arguments.RequireString("profile");
        if (operation == "related") arguments.RequireString("path");
        if (operation == "read" && (!Guid.TryParse(arguments.RequireString("id"), out var id) || id == Guid.Empty)) arguments.Issue("Invalid test run ID.");
        if (arguments.String("filter")?.Length > 2000) arguments.Issue("Test filter is too long.");
        arguments.Validate();
        return operation;
    }
}