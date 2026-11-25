using System.Text;
using System.Text.Json;
using CodeAssist.Tools;

namespace CodeAssist.Mcp;

internal static class McpResultDecoder
{
    public static ToolResult Decode(JsonElement result, ToolContext context, JsonElement? schema)
    {
        JsonElement? structured = result.TryGetProperty("structuredContent", out var value) && value.ValueKind != JsonValueKind.Null ? value.Clone() : null;
        var failed = result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        if (!failed && schema is { } contract)
        {
            if (structured is null) return ToolResult.Error("MCP result omitted its declared structured output.");
            try { new ToolInputSchema(contract.GetRawText()).Validate(structured.Value); }
            catch (ToolArgumentException) { return ToolResult.Error("MCP structured output does not match its declared schema."); }
        }
        if (structured is { } data && Encoding.UTF8.GetByteCount(data.GetRawText()) > context.Limits.MaxAttachmentBytes)
            return ToolResult.Error("MCP structured output exceeds its byte budget.");
        var text = new StringBuilder();
        var attachments = new List<ToolAttachment>();
        long bytes = 0;
        if (structured is { } json) text.AppendLine(json.GetRawText());
        if (result.TryGetProperty("content", out var content))
            foreach (var part in content.EnumerateArray())
            {
                var type = part.GetProperty("type").GetString();
                if (type == "text") text.AppendLine(part.GetProperty("text").GetString());
                else if (type is "image" or "audio") Add(part.GetProperty("data").GetString()!, part.GetProperty("mimeType").GetString()!);
                else if (type == "resource")
                {
                    var resource = part.GetProperty("resource");
                    text.AppendLine(resource.GetProperty("uri").GetString());
                    if (resource.TryGetProperty("text", out var body)) text.AppendLine(body.GetString());
                    else if (resource.TryGetProperty("blob", out var blob)) Add(blob.GetString()!, resource.TryGetProperty("mimeType", out var mime) ? mime.GetString()! : "application/octet-stream");
                }
                else text.AppendLine(part.GetRawText());
            }
        return new(text.ToString().TrimEnd('\r', '\n'), failed, attachments.Count == 0 ? null : attachments, structured);

        void Add(string encoded, string mime)
        {
            if (encoded.Length / 4L * 3 > context.Limits.MaxAttachmentBytes - bytes + 2) throw new IOException("MCP result exceeds its attachment budget.");
            var decoded = Convert.FromBase64String(encoded);
            bytes += decoded.Length;
            if (bytes > context.Limits.MaxAttachmentBytes) throw new IOException("MCP result exceeds its attachment budget.");
            attachments.Add(new(mime, decoded));
            text.AppendLine($"[Attachment: {mime}, {decoded.Length} bytes]");
        }
    }
}