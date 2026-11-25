namespace CodeAssist.Sessions;

public sealed class SessionConflictException(string message) : InvalidOperationException(message);