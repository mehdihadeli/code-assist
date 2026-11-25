namespace CodeAssist.Tools;

public interface ITestFeedback
{
    IReadOnlyList<string> Profiles { get; }
    Task<TestRunFeedback> RunAsync(string profile, string? filter, ToolContext context, CancellationToken cancellationToken);
    Task<TestRunFeedback> ReadAsync(Guid id, ToolContext context, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> RelatedAsync(string path, ToolContext context, CancellationToken cancellationToken);
}