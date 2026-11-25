namespace CodeAssist.Tools;

public sealed record BriefDocument(string Title, string Text, IReadOnlyList<BriefAttachment> Attachments);