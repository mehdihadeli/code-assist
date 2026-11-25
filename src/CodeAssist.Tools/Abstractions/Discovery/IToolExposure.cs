namespace CodeAssist.Tools;

public interface IToolExposure
{
    IReadOnlyList<ToolSpec> Select(IReadOnlyList<ToolSpec> allowed, ToolContext context);
    IReadOnlyList<string> Activate(IReadOnlyList<ToolSpec> tools, ToolContext context);
}