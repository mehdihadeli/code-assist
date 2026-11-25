using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat;

internal sealed class HostedConnectorChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private const string SnapshotKey = "codeassist.hosted.block";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await base.GetResponseAsync(Restore(messages), options, cancellationToken);
        foreach (var message in result.Messages)
            for (var index = 0; index < message.Contents.Count; index++) message.Contents[index] = Preserve(message.Contents[index]);
        return result;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pending = new Dictionary<long, (JsonObject Block, StringBuilder Input)>();
        await foreach (var update in base.GetStreamingResponseAsync(Restore(messages), options, cancellationToken))
        {
            if (update.RawRepresentation is BetaRawMessageStreamEvent frame)
                switch (frame.Value)
                {
                    case BetaRawContentBlockStartEvent { ContentBlock.Value: BetaMcpToolUseBlock call } start:
                        if (pending.Count >= 64 || !pending.TryAdd(start.Index, (JsonSerializer.SerializeToNode(call)!.AsObject(), new()))) throw new IOException("Hosted call stream exceeded its assembly budget.");
                        foreach (var content in update.Contents.OfType<McpServerToolCallContent>().ToArray()) update.Contents.Remove(content);
                        break;
                    case BetaRawContentBlockDeltaEvent { Delta.Value: BetaInputJsonDelta delta } change when pending.TryGetValue(change.Index, out var item):
                        if (item.Input.Length + delta.PartialJson.Length > 65_536) throw new IOException("Hosted call arguments exceed their stream budget.");
                        item.Input.Append(delta.PartialJson);
                        break;
                    case BetaRawContentBlockStopEvent stop when pending.Remove(stop.Index, out var item):
                        if (item.Input.Length > 0) item.Block["input"] = JsonNode.Parse(item.Input.ToString());
                        var block = JsonSerializer.SerializeToElement(item.Block);
                        var completed = new McpServerToolCallContent(block.GetProperty("id").GetString()!, block.GetProperty("name").GetString()!, block.GetProperty("server_name").GetString()!)
                        {
                            Arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(block.GetProperty("input")),
                        };
                        Save(completed, block);
                        update.Contents.Add(completed);
                        break;
                    case BetaRawContentBlockStartEvent { ContentBlock.Value: BetaMcpToolListingBlock listing }:
                        var listingContent = new AIContent();
                        Save(listingContent, JsonSerializer.SerializeToElement(listing));
                        update.Contents.Add(listingContent);
                        break;
                }
            for (var index = 0; index < update.Contents.Count; index++) update.Contents[index] = Preserve(update.Contents[index]);
            yield return update;
        }
        if (pending.Count != 0) throw new IOException("Hosted call stream ended before arguments completed.");
    }

    private static ChatMessage[] Restore(IEnumerable<ChatMessage> messages)
    {
        var history = messages.ToArray();
        foreach (var content in history.SelectMany(message => message.Contents))
            if (content.AdditionalProperties?.TryGetValue(SnapshotKey, out var saved) == true && saved is JsonElement snapshot)
            {
                Validate(snapshot);
                content.RawRepresentation = snapshot.Deserialize<BetaContentBlockParam>() ?? throw new IOException("Invalid hosted history block.");
            }
        return history;
    }

    private static AIContent Preserve(AIContent content)
    {
        if (content.RawRepresentation is not (BetaMcpToolUseBlock or BetaMcpToolResultBlock or BetaMcpToolListingBlock or BetaToolSearchToolResultBlock or BetaServerToolUseBlock)) return content;
        var block = JsonSerializer.SerializeToElement(content.RawRepresentation);
        if (!Supported(block)) return content;
        if (content is not (McpServerToolCallContent or McpServerToolResultContent)) content = new AIContent();
        Save(content, block);
        return content;
    }

    private static void Save(AIContent content, JsonElement snapshot)
    {
        Validate(snapshot);
        (content.AdditionalProperties ??= new())[SnapshotKey] = snapshot.Clone();
    }

    private static void Validate(JsonElement snapshot)
    {
        if (!Supported(snapshot) || Encoding.UTF8.GetByteCount(snapshot.GetRawText()) > 262_144) throw new IOException("Hosted history block is unsupported or exceeds its budget.");
    }

    private static bool Supported(JsonElement snapshot) => snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("type", out var type) &&
        (type.GetString() is "mcp_tool_use" or "mcp_tool_result" or "mcp_tool_listing" or "tool_search_tool_result" ||
         type.GetString() == "server_tool_use" && snapshot.TryGetProperty("name", out var name) && name.GetString() is "tool_search_tool_regex" or "tool_search_tool_bm25");
}