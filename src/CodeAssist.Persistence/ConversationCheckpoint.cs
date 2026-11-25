using CodeAssist.Contracts;

namespace CodeAssist.Persistence;

public sealed record ConversationCheckpoint(Guid Id, long Revision, AgentSessionArchive Archive,
    string Title, bool Archived, long InputTokens, long OutputTokens, long CompletedTurns, long FailedTurns,
    bool TokensReported);