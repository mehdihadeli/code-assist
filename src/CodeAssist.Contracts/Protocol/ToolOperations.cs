namespace CodeAssist.Contracts.Protocol;

public static class ToolOperations
{
    /// <summary>Optional query: <c>allow</c> and <c>deny</c> (repeatable) narrow the list the way a role would.</summary>
    public static readonly OperationDescriptor ListTools = new("catalog.tools.list", "GET", "/v1/catalog/tools");
    public static readonly OperationDescriptor GetTool = new("catalog.tools.get", "GET", "/v1/catalog/tools/{name}");
}
