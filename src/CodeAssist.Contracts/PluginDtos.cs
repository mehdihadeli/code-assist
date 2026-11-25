namespace CodeAssist.Contracts;

/// <summary>Enabled text guidance available to a client, without package paths or executable definitions.</summary>
public sealed record PluginCommandDto(string Name, string Kind, string Description, int DescriptionBytes, int ContentBytes);

/// <summary>Explicitly selected plugin guidance and untrusted task text; expanding it never calls a model.</summary>
public sealed record ExpandPluginRequest(string Name, string Input = "");

/// <summary>Bounded text to submit through the normal conversation permission boundary.</summary>
public sealed record PluginPromptDto(string Text);