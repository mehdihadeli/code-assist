namespace CodeAssist.Configuration;

/// <summary>String-keyed map whose keys compare case-insensitively (matches environment variable semantics).</summary>
public sealed class SettingsMap<T> : Dictionary<string, T>
{
    public SettingsMap() : base(StringComparer.OrdinalIgnoreCase) { }
}
