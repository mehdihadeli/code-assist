using System.Text.Json.Nodes;
using CodeAssist.Configuration;
using Shouldly;

namespace CodeAssist.Configuration.IntegrationTests.Support;

public sealed class SettingsFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ca-settings-" + Guid.NewGuid().ToString("N")[..8]);
    public string Home => Path.Combine(Root, "home");
    public string Managed => Path.Combine(Root, "managed");
    public string Repo => Path.Combine(Root, "repo");
    public string Work => Path.Combine(Repo, "src");

    public SettingsFixture()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Managed);
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Work);
    }

    public SettingsLocations Locations => new(Home, Work, Managed);

    public void User(string json) => File.WriteAllText(Path.Combine(Home, "settings.json"), json);
    public void Preferences(string json) => File.WriteAllText(Path.Combine(Home, "preferences.json"), json);
    public void Managed_(string json) => File.WriteAllText(Path.Combine(Managed, "settings.json"), json);

    public void Project(string json)
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".code-assist"));
        File.WriteAllText(Path.Combine(Repo, ".code-assist", "settings.json"), json);
    }

    public SettingsSnapshot Load(Dictionary<string, string>? env = null, Dictionary<string, string>? flags = null) =>
        SettingsLoader.Load(new SettingsLoadRequest(Locations, env ?? [], flags ?? []));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

