using CodeAssist.Tools;

namespace CodeAssist.Tools;

internal sealed record TeamManagementRequest(string Operation, Guid Team, string? Name, int Revision, TeamMutation Mutation, Guid? Member);