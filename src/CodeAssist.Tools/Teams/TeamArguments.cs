using CodeAssist.Tools;

namespace CodeAssist.Tools;

internal static class TeamArguments
{
    public static Guid Id(ToolArguments arguments, string name)
    {
        if (!Guid.TryParse(arguments.RequireString(name), out var id) || id == Guid.Empty) arguments.Issue($"{name} must be a nonempty UUID.");
        return id;
    }
}