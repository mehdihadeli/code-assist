using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Sessions;

/// <summary>Transport-agnostic tool catalog operations shared by the server and the in-process SDK.</summary>
public sealed class ToolsApi(ToolCatalog catalog)
{
    public IReadOnlyList<ToolInfoDto> List(IReadOnlyCollection<string>? allow = null, IReadOnlyCollection<string>? deny = null) =>
        catalog.Visible(allow, deny).Select(ToDto).ToArray();

    public ToolInfoDto? Get(string name) => catalog.Find(name) is { } tool ? ToDto(tool) : null;

    private static ToolInfoDto ToDto(ITool tool)
    {
        var spec = tool.Spec;
        return new ToolInfoDto(spec.Name, spec.Description, spec.InputSchema, spec.Risk.ToString().ToLowerInvariant(), spec.ReadOnly, spec.Tags);
    }
}
