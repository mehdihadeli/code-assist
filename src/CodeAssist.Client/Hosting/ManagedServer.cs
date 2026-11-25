using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeAssist.Client;

public sealed class ManagedServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenRegistration _shutdown;
    private readonly WindowsProcessJob? _job;
    private readonly object _cleanupGate = new();
    private Task? _cleanup;
    public CodeAssistClient Client { get; }
    public Uri Endpoint { get; }

    private ManagedServer(Process process, WindowsProcessJob? job, Uri endpoint, string token, CancellationToken lifetime)
    {
        _process = process;
        _job = job;
        Endpoint = endpoint;
        Client = new(endpoint, token);
        _shutdown = lifetime.Register(static state => { _ = ((ManagedServer)state!).BeginCleanup(); }, this);
    }

    public static async Task<ManagedServer> StartAsync(string assembly, string workspace,
        IReadOnlyDictionary<string, string> environment, string address = "127.0.0.1:0", CancellationToken cancellation = default, string? token = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Uri.TryCreate($"http://{address}", UriKind.Absolute, out var listener) || !listener.IsLoopback ||
            listener.UserInfo.Length != 0 || listener.AbsolutePath != "/" || listener.Query.Length != 0 || listener.Fragment.Length != 0)
            throw new ArgumentException("HTTP hosting requires a loopback host:port address.", nameof(address));
        if (!File.Exists(assembly)) throw new FileNotFoundException("Bundled CodeAssist.Server is missing. Build or install the complete distribution.");
        token ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        start.ArgumentList.Add(Path.GetFullPath(assembly));
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add(address);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE", "HOME", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_HOST_PATH" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var item in environment.Where(item => !item.Key.Equals("Host__Listen__Http__Token", StringComparison.OrdinalIgnoreCase))) start.Environment[item.Key] = item.Value;
        start.Environment["Host__Listen__Http__Token"] = token;
        start.Environment["PWD"] = workspace;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["CODEASSIST_MANAGED_STDIN"] = "1";
        start.Environment["CODEASSIST_MANAGED_BOOTSTRAP"] = "1";
        var process = new Process { StartInfo = start };
        WindowsProcessJob? job = null;
        ManagedServer? server = null;
        try
        {
            if (!process.Start()) throw new IOException("Server failed to start.");
            job = WindowsProcessJob.Attach(process);
            process.StandardInput.BaseStream.WriteByte(1);
            await process.StandardInput.BaseStream.FlushAsync(cancellation);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var line = new StringBuilder();
            var character = new char[1];
            while (await process.StandardOutput.ReadAsync(character, timeout.Token) != 0)
            {
                if (character[0] == '\n') break;
                if (line.Length >= 65536) throw new InvalidDataException("Invalid server bootstrap.");
                line.Append(character[0]);
            }
            using var bootstrap = JsonDocument.Parse(line.ToString());
            var ready = bootstrap.RootElement;
            if (ready.GetProperty("kind").GetString() != "ready" || ready.GetProperty("protocol").GetString() != "1" ||
                !Uri.TryCreate(ready.GetProperty("endpoint").GetString(), UriKind.Absolute, out var endpoint)) throw new InvalidDataException("Invalid server bootstrap.");
            server = new(process, job, endpoint, token, cancellation);
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            await server.Client.CheckAsync(timeout.Token);
            return server;
        }
        catch (Exception exception)
        {
            try
            {
                if (server is not null) await server.DisposeAsync();
                else
                {
                    job?.Dispose();
                    TryKill(process);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(deadline.Token); }
                    catch (InvalidOperationException) { }
                }
            }
            catch (Exception cleanupError) when (cleanupError is IOException or Win32Exception or OperationCanceledException)
            {
                Trace.TraceError("Managed service startup cleanup failed ({0}).", cleanupError.GetType().Name);
            }
            finally { process.Dispose(); }
            cancellation.ThrowIfCancellationRequested();
            throw new IOException("Server failed to start. Check runtime configuration and the service distribution.", exception);
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellation) => _process.WaitForExitAsync(cancellation);

    public ValueTask DisposeAsync() => new(BeginCleanup());

    private Task BeginCleanup()
    {
        lock (_cleanupGate) return _cleanup ??= CleanupAsync();
    }

    private async Task CleanupAsync()
    {
        await Task.Yield();
        try
        {
            Client.Dispose();
            try
            {
                _process.StandardInput.BaseStream.WriteByte(2);
                _process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
            {
                Trace.TraceError("Managed service pipe close failed ({0}).", exception.GetType().Name);
            }
            if (!await WaitForExitWithinAsync(TimeSpan.FromSeconds(6))) TryKill(_process);
            Exception? containmentError = null;
            if (_job is not null)
            {
                try { await _job.TerminateAndWaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception exception) when (exception is Win32Exception or OperationCanceledException)
                {
                    containmentError = exception;
                    Trace.TraceError("Managed service job cleanup failed ({0}).", exception.GetType().Name);
                    _job.Dispose();
                    TryKill(_process);
                }
            }
            if (!await WaitForExitWithinAsync(TimeSpan.FromSeconds(5)))
                throw new IOException("Managed service did not exit within the shutdown deadline.");
            if (containmentError is not null)
                throw new IOException("Managed service process containment cleanup failed.", containmentError);
        }
        finally
        {
            _job?.Dispose();
            _shutdown.Dispose();
            _process.Dispose();
        }
    }

    private async Task<bool> WaitForExitWithinAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try { await _process.WaitForExitAsync(deadline.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Exception exception) when (exception is Win32Exception or AggregateException)
        {
            Trace.TraceError("Managed service process-tree termination failed ({0}).", exception.GetType().Name);
        }
    }
}