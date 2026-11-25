namespace CodeAssist.Tools;

public enum ToolJobStatus { Running, Completed, Failed, Cancelled, TimedOut }

public sealed record ToolJob(Guid Id, Guid SessionId, string Description, ToolJobStatus Status,
    DateTimeOffset CreatedUtc, ToolResult? Result = null);

public interface IToolJobs
{
    event Action<ToolJob>? Completed;

    ToolJob Start(string description, Func<CancellationToken, Task<ToolResult>> work, ToolContext context, CancellationToken cancellationToken);

    IReadOnlyList<ToolJob> List(Guid sessionId);

    ToolJob? Read(Guid sessionId, Guid id);

    Task<ToolJob?> StopAsync(Guid sessionId, Guid id, CancellationToken cancellationToken);
}