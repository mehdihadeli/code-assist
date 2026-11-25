namespace CodeAssist.Tools;

public sealed record KnowledgeNote(Guid Id, long Revision, string Title, string Body, DateTimeOffset UpdatedUtc);