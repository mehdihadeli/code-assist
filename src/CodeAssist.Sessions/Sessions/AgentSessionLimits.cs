namespace CodeAssist.Sessions;

public sealed record AgentSessionLimits
{
    public int MaxSessions { get; init; } = 64;
    public int MaxConcurrentTurns { get; init; } = 4;
    public int MaxMessages { get; init; } = 128;
    public int MaxInputCharacters { get; init; } = 65_536;
    public int MaxOutputCharacters { get; init; } = 262_144;
    public int MaxHistoryCharacters { get; init; } = 1_048_576;
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public long? MaxSessionTokens { get; init; }
    public int MaxSessionTurns { get; init; } = 128;
}