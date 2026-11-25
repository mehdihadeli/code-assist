using System.Text.Json;

namespace CodeAssist.Contracts;

public sealed record AgentSessionArchive(int Version, string Model, DateTimeOffset CreatedAt,
    AgentMessageDto[] Messages, JsonElement State);