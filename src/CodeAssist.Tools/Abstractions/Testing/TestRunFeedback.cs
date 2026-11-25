namespace CodeAssist.Tools;

public sealed record TestRunFeedback(Guid Id, string Profile, string Status, int? ExitCode, int Total, int Passed, int Failed, int Skipped, IReadOnlyList<TestCaseFeedback> Cases, string Output);