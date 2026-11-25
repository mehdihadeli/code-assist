using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class FlowRunTool(IToolDispatcher dispatcher) : ITool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Binding(string Argument, string Step, string Select = "text");
    private sealed record Step(string Id, string Tool, JsonObject? Arguments = null, string[]? After = null, Binding[]? Bindings = null);
    private sealed record Definition(Step[] Steps, string Reduction = "collect", string Field = "text", string Where = "all", int Parallel = 4, int TimeoutSeconds = 120);

    public ToolSpec Spec { get; } = new("flow.run", "Compose visible tools as a bounded dependency graph. Each child call is independently planned and authorized. Independent reads run concurrently; writes serialize. Bind arguments from text, isError or json:/pointer results. Reductions: collect, filter, project, count, join. No scripts, imports, direct IO or recursion.",
        """
        {"type":"object","properties":{"steps":{"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","properties":{
          "id":{"type":"string"},"tool":{"type":"string"},"arguments":{"type":"object"},"after":{"type":"array","items":{"type":"string"}},
          "bindings":{"type":"array","items":{"type":"object","properties":{"argument":{"type":"string"},"step":{"type":"string"},"select":{"type":"string"}},"required":["argument","step"]}}},"required":["id","tool"]}},
          "reduction":{"type":"string","enum":["collect","filter","project","count","join"]},"field":{"type":"string"},"where":{"type":"string","enum":["all","success","error"]},
          "parallel":{"type":"integer","minimum":1,"maximum":8},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":300}},"required":["steps"],"additionalProperties":false}
        """, ToolRisk.Execute, ["flow", "exec"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = Parse(arguments);
        return new(new ToolPlan([new(ToolEffectKind.Exec, "flow:dispatch")], $"Workflow: {definition.Steps.Length} independently authorized calls. Dynamic child effects are planned after bindings resolve."));
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var definition = Parse(arguments);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(definition.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var results = new Dictionary<string, ToolResult>(StringComparer.Ordinal);
        var remaining = definition.Steps.ToList();
        foreach (var step in remaining)
        {
            var spec = dispatcher.Describe(step.Tool, context);
            if (spec is null || spec.Tags.Contains("flow", StringComparer.OrdinalIgnoreCase))
                return ToolResult.Error($"Workflow tool unavailable or recursive: {step.Tool}");
        }
        try
        {
            while (remaining.Count > 0)
            {
                linked.Token.ThrowIfCancellationRequested();
                var ready = remaining.Where(step => Dependencies(step).All(results.ContainsKey)).ToArray();
                using var parallel = new SemaphoreSlim(definition.Parallel);
                async Task<(string Id, ToolResult Result)> RunAsync(Step step)
                {
                    await parallel.WaitAsync(linked.Token);
                    try
                    {
                        if (Dependencies(step).Any(id => results[id].IsError)) return (step.Id, ToolResult.Error("Skipped: dependency failed."));
                        var input = step.Arguments?.DeepClone().AsObject() ?? new JsonObject();
                        foreach (var binding in step.Bindings ?? []) input[binding.Argument] = Select(results[binding.Step], binding.Select);
                        var child = new ToolContext
                        {
                            Workspace = context.Workspace, SessionId = context.SessionId, CallId = $"{context.CallId}/{step.Id}",
                            Role = context.Role, Outputs = context.Outputs,
                            Limits = context.Limits, ProtectedGlobs = context.ProtectedGlobs, IgnoreGlobs = context.IgnoreGlobs, Progress = context.Progress,
                        };
                        var result = await dispatcher.InvokeAsync(step.Tool, input.ToJsonString(), child, linked.Token);
                        return (step.Id, result);
                    }
                    catch (ToolArgumentException exception) { return (step.Id, ToolResult.Error(exception.Message)); }
                    finally { parallel.Release(); }
                }
                var reads = ready.Where(step => dispatcher.Describe(step.Tool, context)?.ReadOnly == true).ToArray();
                var completed = await Task.WhenAll(reads.Select(RunAsync));
                foreach (var item in completed) results.Add(item.Id, item.Result);
                foreach (var step in ready.Except(reads))
                {
                    var item = await RunAsync(step);
                    results.Add(item.Id, item.Result);
                }
                remaining.RemoveAll(step => results.ContainsKey(step.Id));
            }
            var selected = definition.Steps.Where(step => definition.Where == "all" || (definition.Where == "error") == results[step.Id].IsError).ToArray();
            object output = definition.Reduction switch
            {
                "count" => selected.Length,
                "join" => string.Join('\n', selected.Select(step => Select(results[step.Id], definition.Field)?.ToString())),
                "project" => selected.Select(step => Select(results[step.Id], definition.Field)).ToArray(),
                _ => selected.Select(step => new { step.Id, results[step.Id].Text, results[step.Id].IsError }).ToArray(),
            };
            return ToolSupport.BoundedText(JsonSerializer.Serialize(output, Json), context) with { IsError = results.Values.Any(result => result.IsError) };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { return ToolResult.Error("Workflow exceeded its time budget."); }
        catch (ToolArgumentException exception) { return ToolResult.Error(exception.Message); }
    }

    private static JsonNode? Select(ToolResult result, string selector)
    {
        if (selector == "text") return JsonValue.Create(result.Text);
        if (selector == "isError") return JsonValue.Create(result.IsError);
        if (!selector.StartsWith("json:/", StringComparison.Ordinal)) throw new ToolArgumentException("Selector must be text, isError or json:/pointer.");
        try
        {
            var value = JsonNode.Parse(result.Text);
            foreach (var raw in selector[6..].Split('/'))
            {
                var part = raw.Replace("~1", "/").Replace("~0", "~");
                value = value switch
                {
                    JsonObject properties when properties.ContainsKey(part) => properties[part],
                    JsonArray items when int.TryParse(part, out var index) && index >= 0 && index < items.Count => items[index],
                    _ => throw new ToolArgumentException($"Result selector not found: {selector}"),
                };
            }
            return value?.DeepClone();
        }
        catch (JsonException) { throw new ToolArgumentException("Selected tool result is not JSON."); }
    }

    private static IEnumerable<string> Dependencies(Step step) => (step.After ?? []).Concat((step.Bindings ?? []).Select(binding => binding.Step)).Distinct(StringComparer.Ordinal);

    private static Definition Parse(ToolArguments arguments)
    {
        Definition definition;
        try { definition = arguments.Json.Deserialize<Definition>(Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new ToolArgumentException("Invalid workflow definition."); }
        if (definition.Steps is not { Length: > 0 and <= 32 } || definition.Parallel is < 1 or > 8 || definition.TimeoutSeconds is < 1 or > 300)
            throw new ToolArgumentException("Workflow requires 1-32 steps, parallel 1-8 and timeoutSeconds 1-300.");
        if (definition.Reduction is not ("collect" or "filter" or "project" or "count" or "join") || definition.Where is not ("all" or "success" or "error"))
            throw new ToolArgumentException("Unknown workflow reduction or filter.");
        if (definition.Field is null || (definition.Field is not ("text" or "isError") && !definition.Field.StartsWith("json:/", StringComparison.Ordinal)))
            throw new ToolArgumentException("Invalid workflow field selector.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in definition.Steps)
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Id) || step.Id.Length > 40 || step.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') || !names.Add(step.Id) || string.IsNullOrWhiteSpace(step.Tool))
                throw new ToolArgumentException("Steps require unique safe IDs and tool names.");
            if ((step.Bindings ?? []).Any(binding => binding is null || string.IsNullOrWhiteSpace(binding.Argument) || string.IsNullOrWhiteSpace(binding.Step) || binding.Select is null))
                throw new ToolArgumentException("Invalid result binding.");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Count < names.Count)
        {
            var ready = definition.Steps.Where(step => !visited.Contains(step.Id) && Dependencies(step).All(visited.Contains)).ToArray();
            if (ready.Length == 0) throw new ToolArgumentException("Workflow contains a cycle or missing dependency.");
            foreach (var step in ready) visited.Add(step.Id);
        }
        return definition;
    }
}