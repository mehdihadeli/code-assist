namespace CodeAssist.Tools;

public interface IAgentModel
{
    bool ReportsCost { get; }
    Task<AgentModelReply> RespondAsync(AgentModelRequest request, ToolContext context, CancellationToken cancellationToken);
}