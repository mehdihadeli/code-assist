namespace CodeAssist.Contracts;

/// <summary>Explicit approval to start a configured connector process or network connection.</summary>
public sealed record CreateConnectorConnectionRequest(bool Confirmed = false);

/// <summary>Values for retrieving untrusted prompt messages without invoking a model.</summary>
public sealed record ResolveConnectorPromptRequest(string Name, IReadOnlyDictionary<string, string>? Arguments = null);