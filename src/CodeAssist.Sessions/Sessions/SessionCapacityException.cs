namespace CodeAssist.Sessions;

public sealed class SessionCapacityException(string message) : InvalidOperationException(message);