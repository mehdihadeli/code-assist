using CodeAssist.Persistence;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed record AgentModelReply(string Text, IReadOnlyList<AgentCall> Calls, long InputTokens, long OutputTokens, decimal? CostUsd = null);