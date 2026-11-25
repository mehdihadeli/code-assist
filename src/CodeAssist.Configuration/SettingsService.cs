namespace CodeAssist.Configuration;

/// <param name="Candidate">The freshly loaded snapshot, which differs from <paramref name="Snapshot"/> when the reload was rejected.</param>
public sealed record ReloadResult(SettingsSnapshot Snapshot, bool Accepted, bool Changed, SettingsSnapshot Candidate);

public interface ISettingsProvider
{
    SettingsSnapshot Current { get; }
    event Action<SettingsSnapshot>? Changed;
}

/// <summary>
/// Owns the live snapshot. A reload that produces errors is rejected and the last good snapshot stays in effect,
/// so a half-saved file never breaks a running host.
/// </summary>
public sealed class SettingsService : ISettingsProvider
{
    private readonly Lock _gate = new();
    private readonly IEnvironmentVariables _environment;
    private SettingsSnapshot? _current;

    public SettingsService(SettingsLocations locations, IEnvironmentVariables environment, FlagOverrides flags)
    {
        Locations = locations;
        Flags = flags;
        _environment = environment;
    }

    public SettingsLocations Locations { get; }
    public FlagOverrides Flags { get; }

    public event Action<SettingsSnapshot>? Changed;

    /// <summary>Raised with the candidate when a reload was refused because it had errors.</summary>
    public event Action<SettingsSnapshot>? Rejected;

    public SettingsSnapshot Current
    {
        get
        {
            lock (_gate) return _current ??= Build();
        }
    }

    public ReloadResult Reload()
    {
        SettingsSnapshot candidate;
        bool accepted, changed;
        lock (_gate)
        {
            candidate = Build();
            var previous = _current;
            accepted = candidate.IsValid || previous is null || !previous.IsValid;
            changed = accepted && (previous is null || previous.Id != candidate.Id || previous.IsValid != candidate.IsValid);
            if (accepted) _current = candidate;
        }

        if (!accepted) Rejected?.Invoke(candidate);
        else if (changed) Changed?.Invoke(candidate);
        return new ReloadResult(accepted ? candidate : Current, accepted, changed, candidate);
    }

    private SettingsSnapshot Build() =>
        SettingsLoader.Load(new SettingsLoadRequest(Locations, _environment.GetAll(), new Dictionary<string, string>(Flags.Values)));
}
