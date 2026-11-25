using System.Text.Json;
using CodeAssist.Tools;
using Microsoft.Extensions.AI;

namespace CodeAssist.Sessions;

internal sealed class AgentFunctionDeclaration(ToolSpec spec) : AIFunctionDeclaration
{
    public override string Name => spec.Name;
    public override string Description => spec.Description;
    public override JsonElement JsonSchema { get; } = JsonSerializer.Deserialize<JsonElement>(spec.InputSchema);
}