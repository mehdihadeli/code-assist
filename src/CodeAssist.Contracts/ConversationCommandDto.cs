namespace CodeAssist.Contracts;

public sealed record ConversationCommandDto(string Name, string Description, string Kind, string Origin, string? Hint = null);
public sealed record ExpandConversationCommandRequest(string Name, string Input, IReadOnlyDictionary<string, string>? Values = null);
public sealed record ConversationCommandExpansion(string Text, string? Model, string? Role);