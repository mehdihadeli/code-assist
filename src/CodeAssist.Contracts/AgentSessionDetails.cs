namespace CodeAssist.Contracts;

public sealed record AgentSessionDetails(Guid Id, long Revision, string Title, bool Archived,
    long InputTokens, long OutputTokens, long CompletedTurns, long FailedTurns, bool TokensReported);

public sealed record UpdateSessionRequest(string Title, bool Archived);
public sealed record RewindSessionRequest(long Revision, long ExpectedRevision);