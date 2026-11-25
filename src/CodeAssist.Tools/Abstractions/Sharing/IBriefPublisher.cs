namespace CodeAssist.Tools;

public interface IBriefPublisher
{
    Uri Destination { get; }
    Task<BriefReceipt> PublishAsync(BriefDocument document, ToolContext context, CancellationToken cancellationToken);
}