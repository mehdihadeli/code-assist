using CodeAssist.Persistence;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed record AgentModelRequest(string Model, string Instructions, IReadOnlyList<AgentMessageRecorded> Messages, IReadOnlyList<ToolSpec> Tools, long RemainingTokens);