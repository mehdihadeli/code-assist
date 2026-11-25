namespace CodeAssist.Contracts;

/// <summary>A tool as every surface sees it: enough to list, filter by role and show details. Execution happens in the host.</summary>
public sealed record ToolInfoDto(
    string Name,
    string Description,
    string InputSchema,
    string Risk,
    bool ReadOnly,
    IReadOnlyList<string> Tags);
