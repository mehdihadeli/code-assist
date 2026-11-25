namespace CodeAssist.Cli.Commands.Diagnostics;

/// <summary>Finds an executable the way a shell would (PATH, and PATHEXT on Windows) without running it.</summary>
public static class ExecutableLocator
{
    public static string? Find(string command, IReadOnlyDictionary<string, string> environment, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var extensions = OperatingSystem.IsWindows()
            ? (environment.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return Probe(Path.GetFullPath(command, workingDirectory), extensions);

        var path = environment.GetValueOrDefault("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (Probe(Path.Combine(directory.Trim('"'), command), extensions) is { } found) return found;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static string? Probe(string basePath, string[] extensions)
    {
        if (File.Exists(basePath)) return basePath;
        foreach (var extension in extensions)
            if (extension.Length > 0 && File.Exists(basePath + extension)) return basePath + extension;
        return null;
    }
}
