using System.Text.Json;
using CodeAssist.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeAssist.Mcp;

public sealed class McpElicitationBridge(IUserInteraction interaction, Func<Uri, CancellationToken, Task>? openBrowser = null)
{
    public McpClientOptions CreateOptions(string server, ToolContext context) => new()
    {
        Capabilities = new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Form = new FormElicitationCapability(), Url = openBrowser is null ? null : new UrlElicitationCapability() },
        },
        Handlers = new McpClientHandlers
        {
            ElicitationHandler = async (request, cancellationToken) =>
                (await HandleAsync(server, JsonSerializer.SerializeToElement(request), context, cancellationToken)).Deserialize<ElicitResult>()!,
        },
    };

    public async Task<JsonElement> HandleAsync(string server, JsonElement request, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty || request.ValueKind != JsonValueKind.Object || request.GetRawText().Length > 16_384 ||
            !request.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || message.GetString()!.Length > 4000)
            return Result("decline");
        var mode = request.TryGetProperty("mode", out var kind) ? kind.GetString() : "form";
        if (mode == "url")
        {
            if (openBrowser is null || !request.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address) || address.Scheme != "https" || address.UserInfo.Length != 0 || address.Fragment.Length != 0)
                return Result("decline");
            var answer = await interaction.AskAsync([new("MCP external input", $"Server '{server}' requests: {message.GetString()}\n{address.AbsoluteUri}", [new("Open"), new("Cancel")], false)], context, cancellationToken);
            if (answer is not { Count: 1 } || answer[0].Choices.Count != 1 || answer[0].Choices[0] != "Open") return Result("cancel");
            await openBrowser(address, cancellationToken);
            return Result("accept");
        }
        if (mode != "form" || !request.TryGetProperty("requestedSchema", out var schema) || schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) || type.GetString() != "object" ||
            !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Count() > 16)
            return Result("decline");
        ToolInputSchema validator;
        try { validator = new(schema.GetRawText()); }
        catch (Exception exception) when (exception is ArgumentException or JsonException) { return Result("decline"); }
        var answers = await interaction.AskAsync([new("MCP form", $"Server '{server}' requests: {message.GetString()}\nProvide a JSON object matching this form. Never enter passwords or credentials.\n{schema.GetRawText()}", [], false)], context, cancellationToken);
        if (answers is not { Count: 1 } || answers[0].FreeText is not { Length: > 0 and <= 16_384 } text) return Result("cancel");
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Result("decline");
            validator.Validate(document.RootElement);
            return JsonSerializer.SerializeToElement(new { action = "accept", content = document.RootElement.Clone() });
        }
        catch (Exception exception) when (exception is JsonException or ToolArgumentException) { return Result("decline"); }
    }

    private static JsonElement Result(string action) => JsonSerializer.SerializeToElement(new { action });
}