using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Models;

public sealed class ModelFindTool(IModelCatalog catalog) : ITool
{
    public ToolSpec Spec { get; } = new(
        "model.find", "Search the shared model catalog by text, provider and known capability. Results use exact provider/model references and published metadata, not a promise of account access. Refresh is explicit and requires network access.",
        """
        {"type":"object","properties":{"query":{"type":"string"},"provider":{"type":"string"},
         "capability":{"type":"string","enum":["tools","reasoning","vision","image-output"]},
         "offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100},
         "refresh":{"type":"boolean"}},"additionalProperties":false}
        """, ToolRisk.Network, ["read", "net", "model"]);

    private sealed record Request(string[] Terms, string? Provider, string? Capability, int Offset, int Limit, bool Refresh);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments);
        var effects = new List<ToolEffect> { new(ToolEffectKind.Read, "catalog:models") };
        if (request.Refresh) effects.Add(new(ToolEffectKind.Net, "catalog:refresh"));
        return new(new ToolPlan(effects));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            if (request.Refresh)
            {
                var refresh = await catalog.RefreshAsync(cancellationToken);
                if (refresh.Error is not null) return ToolResult.Error($"Catalog refresh failed: {refresh.Error}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var names = catalog.ListProviders().ToDictionary(provider => provider.Id, provider => provider.Name, StringComparer.OrdinalIgnoreCase);
            var models = catalog.List().Where(model =>
                (request.Provider is null || string.Equals(model.ProviderId, request.Provider, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(names.GetValueOrDefault(model.ProviderId), request.Provider, StringComparison.OrdinalIgnoreCase)) &&
                request.Terms.All(term => $"{model.ProviderId}/{model.ModelId} {model.Name}".Contains(term, StringComparison.OrdinalIgnoreCase)) &&
                (request.Capability switch
                {
                    "tools" => model.ToolCallSupport == true || (model.ToolCallSupport is null && model.ToolCalls),
                    "reasoning" => model.Reasoning,
                    "vision" => model.Input.Contains("image", StringComparer.OrdinalIgnoreCase),
                    "image-output" => model.Output.Contains("image", StringComparer.OrdinalIgnoreCase),
                    _ => true,
                })).OrderBy(model => model.ProviderId, StringComparer.Ordinal).ThenBy(model => model.ModelId, StringComparer.Ordinal).ToArray();
            var page = models.Skip(request.Offset).Take(request.Limit).Select(model => new
            {
                reference = $"{model.ProviderId}/{model.ModelId}", model.Name, model.ContextWindow, model.MaxOutputTokens,
                tools = model.ToolCallSupport ?? (model.ToolCalls ? true : (bool?)null), model.Reasoning,
                model.Input, model.Output, model.Price, model.Status, model.ReasoningEfforts,
            });
            return ToolSupport.BoundedText(JsonSerializer.Serialize(new
            {
                source = "shared catalog; metadata may be unknown; credentials and configured variants are not queried",
                total = models.Length,
                nextOffset = (long)request.Offset + request.Limit < models.Length ? request.Offset + request.Limit : (int?)null,
                models = page,
            }), context);
        }, cancellationToken);
    }

    private static Request Parse(ToolArguments arguments)
    {
        var query = arguments.String("query") ?? "";
        var provider = arguments.String("provider");
        var capability = arguments.String("capability");
        var offset = arguments.Int("offset", 0) ?? 0;
        var limit = arguments.Int("limit", 1, 100) ?? 20;
        var refresh = arguments.Bool("refresh");
        if (capability is not null and not ("tools" or "reasoning" or "vision" or "image-output")) arguments.Issue("Unknown model capability.");
        arguments.Validate();
        return new(query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), provider, capability, offset, limit, refresh);
    }
}