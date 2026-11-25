using System.Text.Json;

namespace CodeAssist.Contracts;

public sealed record ManagementCommandRequest(string Operation, JsonElement Arguments);
public sealed record ManagementReply(JsonElement Value, string Summary, int ExitCode = 0, bool HasValue = true);
public sealed record ToolQueryRequest(IReadOnlyCollection<string>? Allow, IReadOnlyCollection<string>? Deny);

public sealed class PluginManagementArguments
{
    public string? Target { get; init; }
    public string? Id { get; init; }
    public string? Release { get; init; }
    public string? Output { get; init; }
    public string? Digest { get; init; }
    public bool ForWorkspace { get; init; }
    public string Input { get; init; } = "";
    public string? Registry { get; init; }
    public string RegistryName { get; init; } = "local.registry";
    public string Lane { get; init; } = "general";
    public string Query { get; init; } = "";
    public string[] SeenFiles { get; init; } = [];
    public string[] SeenFolders { get; init; } = [];
    public string[] SeenCommands { get; init; } = [];
    public string[] SeenDependencies { get; init; } = [];
}

public sealed class SkillManagementArguments
{
    public string? Id { get; init; }
    public bool Agent { get; init; }
    public string Input { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string[] Files { get; init; } = [];
}

public sealed class ConnectorManagementArguments
{
    public string Name { get; init; } = "";
    public string? Prompt { get; init; }
    public string[] Arguments { get; init; } = [];
}