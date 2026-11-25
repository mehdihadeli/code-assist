using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class StructuredOutputTool : ITool
{
    private readonly IStructuredOutput _output;
    private readonly ToolInputSchema _schema;
    public ToolSpec Spec { get; }

    public StructuredOutputTool(IStructuredOutput output)
    {
        _output = output;
        _schema = new ToolInputSchema(output.InputSchema);
        Spec = new("output.submit", $"Commit structured result for contract '{output.Contract}'. Input must match the supplied schema. Only one result per contract/session; identical retries with the same call ID are idempotent. Does not itself complete an agent turn.",
            output.InputSchema, ToolRisk.Write, ["output", "write"]);
    }

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _schema.Validate(arguments.Json);
        return new(new ToolPlan([new(ToolEffectKind.Write, $"session:{context.SessionId}:output:{_output.Contract}")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        _schema.Validate(arguments.Json);
        return ToolSupport.FromPortAsync(async () => ToolSupport.BoundedText(JsonSerializer.Serialize(
            await _output.SubmitAsync(arguments.Json, context, cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web)), context), cancellationToken);
    }
}