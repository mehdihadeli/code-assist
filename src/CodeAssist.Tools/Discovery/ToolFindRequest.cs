namespace CodeAssist.Tools;

public sealed record ToolFindRequest(string[] Terms, int Offset, int Limit, bool Schemas, string? Server, bool Activate);