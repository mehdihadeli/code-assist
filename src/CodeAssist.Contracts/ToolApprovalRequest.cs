namespace CodeAssist.Contracts;

public sealed record ToolApprovalEffect(string Kind, string Target, bool Outside, bool Protected);
public sealed record ToolApprovalRequest(Guid Id, Guid SessionId, string CallId, string Tool,
    ToolApprovalEffect[] Effects, string? Preview, DateTimeOffset CreatedAt);
public sealed record ResolveToolApprovalRequest(bool Allowed);