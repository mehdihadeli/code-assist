using CodeAssist.Configuration;

namespace CodeAssist.Testing;

/// <summary>Isolated home, managed settings and workspace directories for runtime tests.</summary>
public sealed class SettingsSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ca-cli-" + Guid.NewGuid().ToString("N")[..8]);
    public string Home => Path.Combine(Root, "home");
    public string Managed => Path.Combine(Root, "managed");
    public string Repo => Path.Combine(Root, "repo");
    public string Work => Path.Combine(Repo, "src");

    public SettingsSandbox()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Managed);
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Work);
    }

    public SettingsLocations Locations => new(Home, Work, Managed);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
