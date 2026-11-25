namespace CodeAssist.Configuration;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record SettingsDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Path = null,
    SettingsTier? Tier = null,
    string? Source = null);

public sealed class DiagnosticBag : List<SettingsDiagnostic>
{
    public bool HasErrors => this.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Add(DiagnosticSeverity severity, string code, string message, string? path = null, SettingsTier? tier = null, string? source = null) =>
        Add(new SettingsDiagnostic(severity, code, message, path, tier, source));

    public void Error(string code, string message, string? path = null, SettingsTier? tier = null, string? source = null) =>
        Add(DiagnosticSeverity.Error, code, message, path, tier, source);

    public void Warn(string code, string message, string? path = null, SettingsTier? tier = null, string? source = null) =>
        Add(DiagnosticSeverity.Warning, code, message, path, tier, source);

    public void Info(string code, string message, string? path = null, SettingsTier? tier = null, string? source = null) =>
        Add(DiagnosticSeverity.Info, code, message, path, tier, source);
}

/// <summary>One value that some tier offered for a key, and whether it was accepted.</summary>
public sealed record SettingCandidate(SettingsTier Tier, string Source, string Value, bool Accepted, string? Reason);

public sealed class SettingOrigin(string path)
{
    public string Path { get; } = path;
    public List<SettingCandidate> Candidates { get; } = [];

    public SettingCandidate? Effective => Candidates.LastOrDefault(c => c.Accepted);
    public SettingsTier Tier => Effective?.Tier ?? SettingsTier.Defaults;
    public string Source => Effective?.Source ?? "built-in default";
}
