namespace CodeAssist.Client;

public sealed record ApiResponse(int StatusCode, string? ContentType, string Body);