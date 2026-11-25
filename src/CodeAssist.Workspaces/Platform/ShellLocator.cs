namespace CodeAssist.Workspaces.Platform;

/// <param name="Arguments">Arguments that go before the command text (for example <c>-c</c>).</param>
public sealed record ShellInfo(string Name, string FilePath, IReadOnlyList<string> Arguments);

public static class ShellLocator
{
    public static ShellInfo LocateProfile(string profile, string? configured = null)
    {
        if (profile is not ("bash" or "powershell")) throw new ArgumentException("Unknown shell profile.");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var selected = Describe(configured);
            if (profile == "bash" && selected.Name != "bash" || profile == "powershell" && selected.Name is not ("pwsh" or "powershell"))
                throw new ArgumentException("Configured executable does not match the shell profile.");
            return selected;
        }
        var executable = profile == "bash" ? OperatingSystem.IsWindows() ? "bash.exe" : "bash" : OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var found = FindOnPath(executable);
        if (found is null && profile == "powershell" && OperatingSystem.IsWindows()) found = FindOnPath("powershell.exe");
        return Describe(found ?? throw new IOException($"Shell profile '{profile}' is unavailable; configure its executable path."));
    }

    /// <summary>Picks a shell: the configured one, else PowerShell 7, Windows PowerShell or cmd on Windows, and $SHELL, bash or sh elsewhere.</summary>
    public static ShellInfo Locate(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Describe(configured);

        if (OperatingSystem.IsWindows())
        {
            if (FindOnPath("pwsh.exe") is { } pwsh) return Describe(pwsh);
            if (FindOnPath("powershell.exe") is { } powershell) return Describe(powershell);
            return Describe(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe");
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)) return Describe(fromEnvironment);
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/bin/sh" })
            if (File.Exists(candidate)) return Describe(candidate);
        return Describe("/bin/sh");
    }

    private static ShellInfo Describe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        IReadOnlyList<string> arguments = name switch
        {
            "pwsh" or "powershell" => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command"],
            "cmd" => ["/d", "/s", "/c"],
            _ => ["-c"],
        };
        return new ShellInfo(name, path, arguments);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
