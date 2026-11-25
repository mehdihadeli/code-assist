namespace CodeAssist.Configuration;

/// <summary>Layers in ascending precedence. A higher tier wins unless a trust rule rejects its value.</summary>
public enum SettingsTier { Defaults, Preferences, User, Project, Environment, Flags, Managed }

/// <summary>Who may set a key. Enforced per tier by the loader.</summary>
public enum TrustClass
{
    /// <summary>Harmless behavior; accepted from every tier.</summary>
    Preference,
    /// <summary>Accepted from every tier, but the project tier may only move the value in the strict direction.</summary>
    Tighten,
    /// <summary>Endpoints, credentials, processes, allow rules; never accepted from project files.</summary>
    Sensitive,
    /// <summary>Per-user presentation; never accepted from project files.</summary>
    UserOnly,
    /// <summary>Organization lock list and hard policy; managed tier only.</summary>
    ManagedOnly,
}

public enum StrictDirection
{
    None,
    /// <summary>Lower is stricter (limits, timeouts).</summary>
    Min,
    /// <summary>Higher is stricter.</summary>
    Max,
    /// <summary>List values must be a subset of the current value (allow-lists).</summary>
    Subset,
    /// <summary>Value must appear no later than the current value in the declared order.</summary>
    Order,
}

public enum MergeMode
{
    /// <summary>Objects deep-merge, scalars and lists replace.</summary>
    Replace,
    /// <summary>Lists from higher tiers are appended (deduplicated).</summary>
    Append,
    /// <summary>Ordered policy rules; project tier may only add deny rules; each rule records its origin tier.</summary>
    Rules,
}

public enum ReloadKind { Live, RestartRequired }

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class TrustAttribute(TrustClass value) : Attribute
{
    public TrustClass Value { get; } = value;
}

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class StrictAttribute(StrictDirection direction) : Attribute
{
    public StrictDirection Direction { get; } = direction;
}

/// <summary>Strict-to-loose ordering of enum names for <see cref="StrictDirection.Order"/>.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class StrictOrderAttribute(params string[] strictToLoose) : Attribute
{
    public IReadOnlyList<string> Values { get; } = strictToLoose;
}

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class MergeAttribute(MergeMode mode) : Attribute
{
    public MergeMode Mode { get; } = mode;
}

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ReloadAttribute(ReloadKind kind) : Attribute
{
    public ReloadKind Kind { get; } = kind;
}

/// <summary>Marks a key that must never be stored in a settings file (environment or credential vault only).</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SecretAttribute : Attribute;
