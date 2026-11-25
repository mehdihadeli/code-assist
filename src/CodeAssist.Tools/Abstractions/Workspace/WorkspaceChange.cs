namespace CodeAssist.Tools;

public sealed record WorkspaceChange(string Path, string? OriginalPath, string IndexStatus, string WorktreeStatus, bool Conflict, string? IndexDiff, string? WorktreeDiff);