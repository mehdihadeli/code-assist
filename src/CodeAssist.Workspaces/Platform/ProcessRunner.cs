using System.Diagnostics;
using System.Text;

namespace CodeAssist.Workspaces.Platform;

public sealed record ProcessRequest(
    string FilePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaxOutputChars,
    IReadOnlyCollection<string> AllowedEnvironment);

/// <param name="ExitCode">Null when the process was killed or never started.</param>
/// <param name="Output">Combined stdout and stderr; when long, only the end is kept.</param>
/// <param name="OmittedChars">Characters dropped from the start of the output.</param>
public sealed record ProcessOutcome(int? ExitCode, string Output, int OmittedChars, bool TimedOut, bool Cancelled);

public interface IProcessRunner
{
    Task<ProcessOutcome> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>Runs a child process with a scrubbed environment, bounded output, a timeout, and whole-tree kill.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly string[] SafeEnvironment =
    [
        "PATH", "PATHEXT", "SYSTEMROOT", "SystemDrive", "COMSPEC", "WINDIR", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "USER", "USERNAME", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TZ",
        "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "OS", "PSModulePath", "DOTNET_ROOT", "JAVA_HOME", "GOPATH", "CARGO_HOME", "RUSTUP_HOME",
    ];

    public async Task<ProcessOutcome> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(request.FilePath)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        ScrubEnvironment(info, request.AllowedEnvironment);

        var tail = new TailBuffer(request.MaxOutputChars);
        using var process = new Process { StartInfo = info };

        try { process.Start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessOutcome(null, $"Could not start '{request.FilePath}': {exception.Message}", 0, false, false);
        }

        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stdout = PumpAsync(process.StandardOutput, tail, linked.Token);
        var stderr = PumpAsync(process.StandardError, tail, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr);
            return new ProcessOutcome(process.ExitCode, tail.Text, tail.Omitted, false, false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            var cancelled = cancellationToken.IsCancellationRequested;
            return new ProcessOutcome(null, tail.Text, tail.Omitted, !cancelled, cancelled);
        }
    }

    private static async Task PumpAsync(StreamReader reader, TailBuffer output, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0) output.Append(buffer.AsSpan(0, count));
    }

    private static void ScrubEnvironment(ProcessStartInfo info, IReadOnlyCollection<string> allowed)
    {
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) inherited[key] = value;

        info.Environment.Clear();
        foreach (var name in SafeEnvironment.Concat(allowed))
            if (inherited.TryGetValue(name, out var value)) info.Environment[name] = value;
        info.Environment["NO_COLOR"] = "1";
    }

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private sealed class TailBuffer(int max)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        private int _omitted;

        public int Omitted { get { lock (_gate) return _omitted + Math.Max(0, _builder.Length - max); } }

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    var text = _builder.ToString();
                    return text.Length > max ? text[^max..] : text;
                }
            }
        }

        public void Append(ReadOnlySpan<char> text)
        {
            lock (_gate)
            {
                _builder.Append(text);
                if (_builder.Length > max * 2)
                {
                    var drop = _builder.Length - max;
                    _builder.Remove(0, drop);
                    _omitted += drop;
                }
            }
        }
    }
}
