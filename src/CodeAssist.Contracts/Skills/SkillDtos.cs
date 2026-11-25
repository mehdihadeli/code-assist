namespace CodeAssist.Contracts;

/// <summary>Human-invocable skill metadata without local filesystem paths or content.</summary>
public sealed record SkillCommandDto(string Id, string Description, string Origin, string? Title, string? Hint, IReadOnlyList<string> Parameters);

/// <summary>Explicit skill expansion data; the caller identity and workspace are server-owned.</summary>
public sealed record ExpandSkillRequest(string Id, string Input = "", IReadOnlyList<string>? Arguments = null);

/// <summary>Expanded text guidance, with no tool execution or model inference.</summary>
public sealed record SkillPromptDto(string Text);