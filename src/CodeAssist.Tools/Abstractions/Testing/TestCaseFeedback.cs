namespace CodeAssist.Tools;

public sealed record TestCaseFeedback(string Id, string Name, string Outcome, string? Message, string? StackTrace);