namespace CodeAssist.Tools;

public sealed record RetrievalMatch(string Path, int StartLine, int EndLine, string Sha256, double Score, string Text);