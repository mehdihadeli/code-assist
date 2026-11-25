using System.Runtime.InteropServices;

namespace CodeAssist.Configuration;

/// <summary>Read-only view of process environment variables (replaceable in tests).</summary>
public interface IEnvironmentVariables
{
    IReadOnlyDictionary<string, string> GetAll();
}

public sealed class ProcessEnvironmentVariables : IEnvironmentVariables
{
    public IReadOnlyDictionary<string, string> GetAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) result[key] = value;
        return result;
    }
}

public sealed class DictionaryEnvironmentVariables(IReadOnlyDictionary<string, string> values) : IEnvironmentVariables
{
    public IReadOnlyDictionary<string, string> GetAll() => values;
}

/// <summary>Where each tier's files live. Resolved once per process (or per test).</summary>
public sealed record SettingsLocations(string Home, string WorkingDirectory, string ManagedDirectory)
{
    public const string HomeVariable = "CODE_ASSIST_HOME";
    public const string ManagedVariable = "CODE_ASSIST_MANAGED_DIR";

    public string StateDirectory => Path.Combine(Home, "state");
    public string PreferencesFile => Path.Combine(Home, "preferences.json");

    /// <summary>Existing user settings file (settings.json or settings.jsonc), else the default path for a new one.</summary>
    public string UserFile => ExistingOrDefault(Home);

    public string ManagedFile => ExistingOrDefault(ManagedDirectory);

    /// <summary>Project files from the repository root down to the working directory (nearest last).</summary>
    public IReadOnlyList<string> ProjectFiles()
    {
        var found = new List<string>();
        var userHome = Path.GetFullPath(Home).TrimEnd(Path.DirectorySeparatorChar);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = new DirectoryInfo(WorkingDirectory);
        while (directory is not null)
        {
            var settingsDirectory = Path.Combine(directory.FullName, ".code-assist");
            if (!string.Equals(Path.GetFullPath(settingsDirectory).TrimEnd(Path.DirectorySeparatorChar), userHome, StringComparison.OrdinalIgnoreCase))
            {
                var file = FirstExisting(settingsDirectory);
                if (file is not null) found.Add(file);
            }
            var isRepositoryRoot = Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"));
            if (isRepositoryRoot || string.Equals(directory.FullName.TrimEnd(Path.DirectorySeparatorChar), profile.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                break;
            directory = directory.Parent;
        }
        found.Reverse();
        return found;
    }

    /// <summary>The project settings file that scope "project" edits: the nearest existing one, else a new file in the working directory.</summary>
    public string ProjectFileForWrite() =>
        ProjectFiles().LastOrDefault() ?? Path.Combine(WorkingDirectory, ".code-assist", "settings.json");

    public static SettingsLocations Resolve(IReadOnlyDictionary<string, string> environment, string workingDirectory)
    {
        var home = environment.TryGetValue(HomeVariable, out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".code-assist");

        var managed = environment.TryGetValue(ManagedVariable, out var managedConfigured) && !string.IsNullOrWhiteSpace(managedConfigured)
            ? Path.GetFullPath(managedConfigured)
            : DefaultManagedDirectory();

        return new SettingsLocations(home, Path.GetFullPath(workingDirectory), managed);
    }

    public static string DefaultManagedDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CodeAssist");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "/Library/Application Support/CodeAssist";
        return "/etc/code-assist";
    }

    private static string ExistingOrDefault(string directory) => FirstExisting(directory) ?? Path.Combine(directory, "settings.json");

    private static string? FirstExisting(string directory)
    {
        foreach (var name in new[] { "settings.jsonc", "settings.json" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
