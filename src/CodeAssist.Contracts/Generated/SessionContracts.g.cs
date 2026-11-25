#nullable enable

namespace CodeAssist.Contracts;

public sealed record AgentSessionDto(Guid Id, string Model, DateTimeOffset CreatedAt, bool Running, int MessageCount);

public sealed record AgentMessageDto(int Sequence, string Role, string Text, DateTimeOffset CreatedAt);

public sealed record AgentTurnEvent(Guid TurnId, int Sequence, string Kind, string? Text);

public sealed record CreateAgentSessionRequest(string? Model = null);

public sealed record SendAgentMessageRequest(string Text);

public sealed record CancelAgentTurnResponse(bool Canceled);

public sealed record ServerHealthDto(string Application, string Protocol, string Status);
