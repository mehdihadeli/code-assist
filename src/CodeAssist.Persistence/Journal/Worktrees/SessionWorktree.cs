using CodeAssist.Contracts;
namespace CodeAssist.Persistence;

public sealed record SessionWorktree(string Original, string? Directory, bool Active, bool CleanupPending);