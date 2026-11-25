using CodeAssist.Persistence;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class WorkItemsTool(IWorkLedger ledger) : ITool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<WorkState>(JsonNamingPolicy.CamelCase) },
    };

    public ToolSpec Spec { get; } = new("work.items", "Create, get, page, replace or remove durable session work items. Replacement supplies the complete item and its current revision. Owners are labels, not permissions; active/done items require completed prerequisites.",
        """
        {"type":"object","properties":{"operation":{"enum":["create","get","list","replace","remove"]},
        "id":{"type":"string","format":"uuid"},"revision":{"type":"integer","minimum":1},
        "title":{"type":"string","minLength":1,"maxLength":200},"detail":{"type":"string","maxLength":16000},
        "owner":{"type":"string","maxLength":120},"state":{"enum":["queued","active","done","cancelled"]},
        "requires":{"type":"array","maxItems":100,"uniqueItems":true,"items":{"type":"string","format":"uuid"}},
        "offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100}},
        "required":["operation"],"additionalProperties":false}
        """, ToolRisk.Write, ["tasks", "write"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments);
        return new(new ToolPlan([new(request.Operation is "get" or "list" ? ToolEffectKind.Read : ToolEffectKind.Write, $"session:{context.SessionId}:work")]));
    }

    public Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var request = Parse(arguments);
        return ToolSupport.FromPortAsync(async () =>
        {
            object output;
            if (request.Operation == "create") output = await ledger.CreateAsync(request.Draft!, context, cancellationToken);
            else if (request.Operation == "replace") output = await ledger.ReplaceAsync(request.Id, request.Revision, request.Draft!, request.State, context, cancellationToken);
            else if (request.Operation == "remove")
            {
                await ledger.RemoveAsync(request.Id, request.Revision, context, cancellationToken);
                output = new { removed = request.Id };
            }
            else
            {
                var items = await ledger.ListAsync(context, cancellationToken);
                if (request.Operation == "get")
                {
                    var item = items.FirstOrDefault(item => item.Id == request.Id);
                    if (item is null) return ToolResult.Error("Work item not found in current session.");
                    output = item;
                }
                else output = new { total = items.Count, nextOffset = request.Offset + request.Limit < items.Count ? (int?)(request.Offset + request.Limit) : null,
                    items = items.Skip(request.Offset).Take(request.Limit) };
            }
            return ToolSupport.BoundedText(JsonSerializer.Serialize(output, Json), context);
        }, cancellationToken);
    }

    private sealed record Request(string Operation, Guid Id, int Revision, WorkDraft? Draft, WorkState State, int Offset, int Limit);

    private static Request Parse(ToolArguments arguments)
    {
        var operation = arguments.RequireString("operation");
        if (operation is not ("create" or "get" or "list" or "replace" or "remove")) arguments.Issue("Unknown work operation.");
        var id = Guid.Empty;
        if (operation is "get" or "replace" or "remove" && (!Guid.TryParse(arguments.RequireString("id"), out id) || id == Guid.Empty)) arguments.Issue("id must be a nonempty UUID.");
        var revision = arguments.Int("revision", 1) ?? 0;
        if (operation is "replace" or "remove" && revision == 0) arguments.Issue("revision is required for replacement or removal.");
        var state = arguments.String("state") switch
        {
            null or "queued" => WorkState.Queued,
            "active" => WorkState.Active,
            "done" => WorkState.Done,
            "cancelled" => WorkState.Cancelled,
            _ => (WorkState?)null,
        };
        if (state is null) arguments.Issue("Unknown work state.");
        if (operation == "replace" && !arguments.Has("state")) arguments.Issue("Replacement requires state.");
        if (operation == "create" && state is not WorkState.Queued) arguments.Issue("New work must be queued.");
        WorkDraft? draft = null;
        if (operation is "create" or "replace")
        {
            var title = arguments.RequireString("title");
            var detail = arguments.RequireString("detail");
            var owner = arguments.String("owner");
            var dependencies = new List<Guid>();
            if (arguments.Has("requires"))
            {
                var value = arguments.Json.GetProperty("requires");
                if (value.ValueKind != JsonValueKind.Array) arguments.Issue("requires must be an array of UUIDs.");
                else foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out var dependency) || dependency == Guid.Empty)
                        arguments.Issue("Each prerequisite must be a nonempty UUID.");
                    else dependencies.Add(dependency);
                }
            }
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl) || detail.Length > 16000 ||
                owner?.Length > 120 || owner?.Any(char.IsControl) == true || dependencies.Count > 100 || dependencies.Distinct().Count() != dependencies.Count)
                arguments.Issue("Invalid work title, detail, owner or prerequisites.");
            draft = new(title, detail, owner, dependencies);
        }
        var offset = arguments.Int("offset", 0, int.MaxValue - 100) ?? 0;
        var limit = arguments.Int("limit", 1, 100) ?? 20;
        arguments.Validate();
        return new(operation, id, revision, draft, state ?? WorkState.Queued, offset, limit);
    }
}