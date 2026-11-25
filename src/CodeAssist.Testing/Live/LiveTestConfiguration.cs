namespace CodeAssist.Testing.Live;

/// <summary>
/// Settings for tests that call real providers. Values come from the process environment first, then the .env file
/// at the repository root (the folder holding CodeAssist.slnx). Live tests run only when <c>ENABLED_LIVE_TESTS</c> is 1 or true.
/// </summary>
public sealed class LiveTestConfiguration
{
    public const string EnabledVariable = "ENABLED_LIVE_TESTS";

    public IReadOnlyDictionary<string, string> Environment { get; }
    public string? RepositoryDirectory { get; }

    public LiveTestConfiguration(IReadOnlyDictionary<string, string> values, string? repositoryDirectory = null)
    {
        Environment = new Dictionary<string, string>(values, StringComparer.Ordinal);
        RepositoryDirectory = repositoryDirectory;
    }

    public bool Enabled => Value(EnabledVariable) is { } flag && (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase));

    public string? Value(string name) => Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <param name="environment">Defaults to the process environment.</param>
    /// <param name="startDirectory">Where to start looking for the repository root; defaults to the current directory.</param>
    public static LiveTestConfiguration Load(IReadOnlyDictionary<string, string>? environment = null, string? startDirectory = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = FindRepositoryRoot(startDirectory ?? Directory.GetCurrentDirectory());
        if (root is not null)
        {
            foreach (var (name, value) in DotEnvFile.Read(Path.Combine(root, ".env"))) merged[name] = value;
        }

        foreach (var (name, value) in environment ?? ProcessEnvironment())
        {
            merged[name] = value;
        }
        return new LiveTestConfiguration(merged, root);
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeAssist.slnx"))) return directory.FullName;
        }
        return null;
    }

    private static Dictionary<string, string> ProcessEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) result[key] = value;
        }
        return result;
    }
}
