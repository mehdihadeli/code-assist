namespace CodeAssist.Cli.Hosting;

/// <summary>Process exit codes. Scripts can rely on these staying stable.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageOrConfig = 2;
    public const int ApprovalNeeded = 3;
    public const int ProviderOrAuth = 4;
    public const int Interrupted = 130;
}

/// <summary>An error caused by how the command was invoked or by invalid configuration; reported without a stack trace.</summary>
public sealed class UsageException(string message, int exitCode = ExitCodes.UsageOrConfig) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
