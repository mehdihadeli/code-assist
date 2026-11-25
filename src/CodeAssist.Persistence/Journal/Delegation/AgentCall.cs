using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record AgentCall(string Id, string Name, string Arguments);