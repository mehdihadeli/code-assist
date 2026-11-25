using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools;

internal sealed record ProcessToolRequest(string Command, ResolvedPath Cwd, TimeSpan Timeout, bool Background, string Shell);