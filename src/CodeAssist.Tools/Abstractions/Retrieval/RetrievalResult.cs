namespace CodeAssist.Tools;

public sealed record RetrievalResult(string Strategy, string? Provider, bool ScanComplete, int ScannedFiles, int SkippedFiles, IReadOnlyList<RetrievalMatch> Matches);