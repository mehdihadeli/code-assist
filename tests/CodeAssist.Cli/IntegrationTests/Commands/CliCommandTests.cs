using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using CodeAssist.Client;
using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;

using CodeAssist.Cli.IntegrationTests.Support;

namespace CodeAssist.Cli.IntegrationTests.Commands;

/// <summary>Runs the real command tree against a temp home, a fake environment and a test console.</summary>
public sealed class CliCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Should_StopJobDescendant_When_ManagedRootAlreadyExited()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = LifecycleDeadline();
        using var sentinel = StartLifecycleProbe("hold");
        using var root = StartLifecycleProbe("root");
        Process? descendant = null;
        IDisposable? job = null;
        ManagedServer? server = null;
        try
        {
            await ReadProbePidAsync(sentinel, deadline.Token);
            await ReadProbePidAsync(root, deadline.Token);
            job = AttachLifecycleJob(root);
            descendant = await SpawnProbeDescendantAsync(root, deadline.Token);
            using var lifetime = new CancellationTokenSource();
            server = CreateLifecycleServer(root, job, lifetime.Token);
            root.StandardInput.WriteLine("exit");
            await root.StandardInput.FlushAsync(deadline.Token);
            await root.WaitForExitAsync(deadline.Token);
            root.ExitCode.ShouldBe(0);
            descendant.HasExited.ShouldBeFalse();
            await Task.WhenAll(lifetime.CancelAsync(), server.DisposeAsync().AsTask()).WaitAsync(deadline.Token);
            await descendant.WaitForExitAsync(deadline.Token);
            descendant.ExitCode.ShouldBe(130);
            sentinel.HasExited.ShouldBeFalse();
            server.DisposeAsync().AsTask().IsCompletedSuccessfully.ShouldBeTrue();
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            job?.Dispose();
            await StopProbeAsync(descendant);
            await StopProbeAsync(root);
            await StopProbeAsync(sentinel);
            descendant?.Dispose();
        }
    }

    [Fact]
    public async Task Should_EscalateWithinShutdownDeadline_When_ManagedRootIgnoresPipeShutdown()
    {
        using var deadline = LifecycleDeadline();
        using var sentinel = StartLifecycleProbe("hold");
        using var root = StartLifecycleProbe("root");
        Process? descendant = null;
        IDisposable? job = null;
        ManagedServer? server = null;
        try
        {
            await ReadProbePidAsync(sentinel, deadline.Token);
            await ReadProbePidAsync(root, deadline.Token);
            if (OperatingSystem.IsWindows()) job = AttachLifecycleJob(root);
            descendant = await SpawnProbeDescendantAsync(root, deadline.Token);
            using var rootObserver = Process.GetProcessById(root.Id);
            _ = rootObserver.Handle;
            server = CreateLifecycleServer(root, job, CancellationToken.None);
            var elapsed = Stopwatch.StartNew();
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(17), deadline.Token);
            elapsed.Stop();
            await descendant.WaitForExitAsync(deadline.Token);
            await rootObserver.WaitForExitAsync(deadline.Token);
            elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(17));
            rootObserver.ExitCode.ShouldNotBe(0);
            descendant.ExitCode.ShouldNotBe(0);
            sentinel.HasExited.ShouldBeFalse();
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            job?.Dispose();
            await StopProbeAsync(descendant);
            await StopProbeAsync(root);
            await StopProbeAsync(sentinel);
            descendant?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_KillJobDescendantsAndPreserveUnrelatedProcess_When_OwnerIsForceKilled(bool rootAlreadyExited)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = LifecycleDeadline();
        using var sentinel = StartLifecycleProbe("hold");
        using var owner = StartLifecycleProbe("owner", typeof(ManagedServer).Assembly.Location, rootAlreadyExited ? "exit" : "hold");
        Process? root = null;
        Process? descendant = null;
        try
        {
            await ReadProbePidAsync(sentinel, deadline.Token);
            await ReadProbePidAsync(owner, deadline.Token);
            var line = await owner.StandardOutput.ReadLineAsync(deadline.Token);
            line.ShouldNotBeNull();
            var pids = line.Split(':').Select(int.Parse).ToArray();
            pids.Length.ShouldBe(2);
            if (!rootAlreadyExited)
            {
                root = Process.GetProcessById(pids[0]);
                _ = root.Handle;
                root.HasExited.ShouldBeFalse();
            }
            descendant = Process.GetProcessById(pids[1]);
            _ = descendant.Handle;
            descendant.HasExited.ShouldBeFalse();
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(deadline.Token);
            await descendant.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            descendant.ExitCode.ShouldBe(0);
            if (root is not null)
            {
                await root.WaitForExitAsync(deadline.Token);
                root.ExitCode.ShouldBe(0);
            }
            owner.ExitCode.ShouldNotBe(0);
            sentinel.HasExited.ShouldBeFalse();
        }
        finally
        {
            await StopProbeAsync(owner);
            await StopProbeAsync(descendant);
            await StopProbeAsync(root);
            await StopProbeAsync(sentinel);
            descendant?.Dispose();
            root?.Dispose();
        }
    }

    [Fact]
    public async Task Should_RejectAssignmentWithoutLaunchingDescendants_When_RootExitedBeforeAttach()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var deadline = LifecycleDeadline();
        using var root = StartLifecycleProbe("root");
        using var sentinel = StartLifecycleProbe("hold");
        try
        {
            await ReadProbePidAsync(root, deadline.Token);
            await ReadProbePidAsync(sentinel, deadline.Token);
            _ = root.Handle;
            root.StandardInput.Close();
            await root.WaitForExitAsync(deadline.Token);
            root.ExitCode.ShouldBe(0);
            var exception = Should.Throw<System.Reflection.TargetInvocationException>(() => AttachLifecycleJob(root));
            exception.InnerException.ShouldBeOfType<System.ComponentModel.Win32Exception>();
            (await root.StandardOutput.ReadToEndAsync(deadline.Token)).ShouldBeEmpty();
            sentinel.HasExited.ShouldBeFalse();
        }
        finally
        {
            await StopProbeAsync(root);
            await StopProbeAsync(sentinel);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_LeaveNoChildAndPreserveCause_When_ManagedBootstrapFails(bool cancelStartup)
    {
        using var deadline = LifecycleDeadline();
        using var startupLifetime = new CancellationTokenSource();
        using var sentinel = StartLifecycleProbe("hold");
        Process? root = null;
        Process? descendant = null;
        Task<ManagedServer>? startup = null;
        try
        {
            await ReadProbePidAsync(sentinel, deadline.Token);
            var pidFile = Path.Combine(_cli.Files.Home, "bootstrap-pids.txt");
            var environment = LifecycleEnvironment();
            environment["LIFECYCLE_PROBE_MODE"] = "invalid-bootstrap";
            environment["LIFECYCLE_PID_FILE"] = pidFile;
            startup = ManagedServer.StartAsync(LifecycleProbeAssembly(), _cli.Files.Locations.WorkingDirectory,
                environment, cancellation: startupLifetime.Token);
            while (!File.Exists(pidFile))
            {
                if (startup.IsCompleted) await startup;
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            var pids = (await File.ReadAllTextAsync(pidFile, deadline.Token)).Split(':').Select(int.Parse).ToArray();
            pids.Length.ShouldBe(2);
            root = Process.GetProcessById(pids[0]);
            descendant = Process.GetProcessById(pids[1]);
            _ = root.Handle;
            _ = descendant.Handle;
            root.HasExited.ShouldBeFalse();
            descendant.HasExited.ShouldBeFalse();
            if (cancelStartup)
            {
                await startupLifetime.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(() => startup.WaitAsync(deadline.Token));
            }
            else
            {
                await File.WriteAllTextAsync(pidFile + ".release", "release", deadline.Token);
                var exception = await Should.ThrowAsync<IOException>(() => startup.WaitAsync(deadline.Token));
                exception.InnerException.ShouldBeAssignableTo<JsonException>();
            }
            await root.WaitForExitAsync(deadline.Token);
            await descendant.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            if (OperatingSystem.IsWindows())
            {
                root.ExitCode.ShouldBe(0);
                descendant.ExitCode.ShouldBe(0);
            }
            else
            {
                root.ExitCode.ShouldNotBe(0);
                descendant.ExitCode.ShouldNotBe(0);
            }
            sentinel.HasExited.ShouldBeFalse();
        }
        finally
        {
            await startupLifetime.CancelAsync();
            await StopProbeAsync(descendant);
            await StopProbeAsync(root);
            await StopProbeAsync(sentinel);
            descendant?.Dispose();
            root?.Dispose();
        }
    }

    private static CancellationTokenSource LifecycleDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return deadline;
    }

    private static string LifecycleProbeAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        directory.ShouldNotBeNull();
        var assembly = Path.Combine(directory.FullName, ".git", "testagent", "managed-lifecycle-2054", "probe", "out", "LifecycleProbe.dll");
        File.Exists(assembly).ShouldBeTrue("Build retained .git/testagent/managed-lifecycle-2054/probe/LifecycleProbe.csproj fixture first.");
        return assembly;
    }

    private static Process StartLifecycleProbe(string mode, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Remove("LIFECYCLE_PROBE_MODE");
        start.ArgumentList.Add(LifecycleProbeAssembly());
        start.ArgumentList.Add(mode);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task ReadProbePidAsync(Process process, CancellationToken cancellation)
    {
        var line = await process.StandardOutput.ReadLineAsync(cancellation);
        line.ShouldBe(process.Id.ToString());
        _ = process.Handle;
    }

    private static async Task<Process> SpawnProbeDescendantAsync(Process root, CancellationToken cancellation)
    {
        root.StandardInput.WriteLine("spawn");
        await root.StandardInput.FlushAsync(cancellation);
        var line = await root.StandardOutput.ReadLineAsync(cancellation);
        line.ShouldNotBeNull();
        var descendant = Process.GetProcessById(int.Parse(line));
        _ = descendant.Handle;
        descendant.HasExited.ShouldBeFalse();
        return descendant;
    }

    private static IDisposable AttachLifecycleJob(Process process) =>
        (IDisposable)typeof(ManagedServer).Assembly.GetType("CodeAssist.Client.WindowsProcessJob", throwOnError: true)!
            .GetMethod("Attach", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [process])!;

    private static ManagedServer CreateLifecycleServer(Process root, IDisposable? job, CancellationToken lifetime) =>
        (ManagedServer)typeof(ManagedServer).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single().Invoke([root, job, new Uri("http://127.0.0.1:1"), new string('t', 40), lifetime]);

    private static async Task StopProbeAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (InvalidOperationException) { }
    }

    [Fact]
    public async Task Should_CompleteSharedCleanup_When_CancellationAndDisposalRaceAfterRootExit()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var lifetime = new CancellationTokenSource();
        await using var server = await ManagedServer.StartAsync(
            Path.Combine(AppContext.BaseDirectory, "service", "CodeAssist.Server.dll"),
            _cli.Files.Locations.WorkingDirectory, LifecycleEnvironment(), cancellation: lifetime.Token);
        var owned = (Process)typeof(ManagedServer).GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        using var observer = Process.GetProcessById(owned.Id);
        _ = observer.Handle;
        owned.StandardInput.BaseStream.WriteByte(2);
        await owned.StandardInput.BaseStream.FlushAsync(deadline.Token);
        await observer.WaitForExitAsync(deadline.Token);
        observer.ExitCode.ShouldBe(0);
        using var release = new ManualResetEventSlim();
        var disposals = Enumerable.Range(0, 12).Select(_ => Task.Factory.StartNew(() =>
        {
            release.Wait(deadline.Token);
            return server.DisposeAsync().AsTask();
        }, deadline.Token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)).ToArray();
        var cancellation = Task.Run(async () =>
        {
            release.Wait(deadline.Token);
            await lifetime.CancelAsync();
        }, deadline.Token);
        release.Set();
        var cleanupTasks = await Task.WhenAll(disposals).WaitAsync(deadline.Token);
        await Task.WhenAll(cleanupTasks).WaitAsync(deadline.Token);
        await cancellation.WaitAsync(deadline.Token);
        var cleanup = server.DisposeAsync().AsTask();
        foreach (var task in cleanupTasks) task.ShouldBeSameAs(cleanup);
        cleanup.IsCompletedSuccessfully.ShouldBeTrue();
        server.DisposeAsync().AsTask().ShouldBeSameAs(cleanup);
        await Should.ThrowAsync<ObjectDisposedException>(() => server.Client.CheckAsync(deadline.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ShareCleanupAndReleaseDllLocks_When_DisposalRacesCancellation(bool cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var lifetime = new CancellationTokenSource();
        var distribution = Path.Combine(_cli.Files.Home, "lifecycle-service");
        foreach (var source in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "service"), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(distribution, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "service"), source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        await using var server = await ManagedServer.StartAsync(Path.Combine(distribution, "CodeAssist.Server.dll"),
            _cli.Files.Locations.WorkingDirectory, LifecycleEnvironment(), cancellation: lifetime.Token);
        var owned = (Process)typeof(ManagedServer).GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        using var observer = Process.GetProcessById(owned.Id);
        _ = observer.Handle;
        using var release = new ManualResetEventSlim();
        var disposals = Enumerable.Range(0, 12).Select(_ => Task.Factory.StartNew(() =>
        {
            release.Wait(deadline.Token);
            return server.DisposeAsync().AsTask();
        }, deadline.Token, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)).ToArray();
        var cancellation = Task.Run(async () =>
        {
            release.Wait(deadline.Token);
            if (cancel) await lifetime.CancelAsync();
        }, deadline.Token);
        release.Set();
        var cleanupTasks = await Task.WhenAll(disposals).WaitAsync(deadline.Token);
        await Task.WhenAll(cleanupTasks).WaitAsync(deadline.Token);
        await cancellation.WaitAsync(deadline.Token);
        var cleanup = server.DisposeAsync().AsTask();
        foreach (var task in cleanupTasks) task.ShouldBeSameAs(cleanup);
        cleanup.IsCompletedSuccessfully.ShouldBeTrue();
        server.DisposeAsync().AsTask().ShouldBeSameAs(cleanup);
        await observer.WaitForExitAsync(deadline.Token);
        observer.ExitCode.ShouldBe(0);
        var libraries = Directory.GetFiles(distribution, "*.dll", SearchOption.AllDirectories);
        libraries.Length.ShouldBeGreaterThan(1);
        foreach (var library in libraries)
        {
            using var exclusive = new FileStream(library, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            exclusive.Length.ShouldBeGreaterThan(0);
        }
    }

    private Dictionary<string, string> LifecycleEnvironment() => new()
    {
        [SettingsLocations.HomeVariable] = _cli.Files.Locations.Home,
        [SettingsLocations.ManagedVariable] = _cli.Files.Locations.ManagedDirectory,
        ["Host__RecoverConversations"] = "false",
        ["ENABLED_LIVE_TESTS"] = "0",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_GateStartupAndExitGracefully_When_BootstrapParentStops(bool closePipe)
    {
        using var deadline = LifecycleDeadline();
        const string token = "tttttttttttttttttttttttttttttttttttttttt";
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _cli.Files.Locations.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "service", "CodeAssist.Server.dll"));
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("127.0.0.1:0");
        foreach (var item in LifecycleEnvironment()) start.Environment[item.Key] = item.Value;
        start.Environment["Host__Listen__Http__Token"] = token;
        start.Environment["CODEASSIST_MANAGED_STDIN"] = "1";
        start.Environment["CODEASSIST_MANAGED_BOOTSTRAP"] = "1";
        using var process = Process.Start(start)!;
        try
        {
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            var bootstrap = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask();
            await Should.ThrowAsync<TimeoutException>(() => bootstrap.WaitAsync(TimeSpan.FromMilliseconds(500), deadline.Token));
            process.HasExited.ShouldBeFalse();
            process.StandardInput.BaseStream.WriteByte(1);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            var line = await bootstrap.WaitAsync(deadline.Token);
            line.ShouldNotBeNull();
            using var ready = JsonDocument.Parse(line);
            ready.RootElement.GetProperty("kind").GetString().ShouldBe("ready");
            ready.RootElement.GetProperty("protocol").GetString().ShouldBe("1");
            using var client = new CodeAssistClient(new Uri(ready.RootElement.GetProperty("endpoint").GetString()!), token);
            await client.CheckAsync(deadline.Token);
            if (closePipe) process.StandardInput.Close();
            else
            {
                process.StandardInput.BaseStream.WriteByte(2);
                await process.StandardInput.BaseStream.FlushAsync(deadline.Token);
            }
            await process.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(8), deadline.Token);
            process.ExitCode.ShouldBe(0);
            (await errors).ShouldNotContain("Server startup or execution failed");
        }
        finally { await StopProbeAsync(process); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_StopOnlyManagedServer_When_ParentPipeCloses(bool managed)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = new string('t', 40);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _cli.Files.Locations.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "service", "CodeAssist.Server.dll"));
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("127.0.0.1:0");
        start.Environment[SettingsLocations.HomeVariable] = _cli.Files.Locations.Home;
        start.Environment[SettingsLocations.ManagedVariable] = _cli.Files.Locations.ManagedDirectory;
        start.Environment["Host__Listen__Http__Token"] = token;
        start.Environment["Host__RecoverConversations"] = "false";
        start.Environment["ENABLED_LIVE_TESTS"] = "0";
        start.Environment["CODEASSIST_MANAGED_STDIN"] = managed ? "1" : "0";
        using var process = Process.Start(start)!;
        try
        {
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token);
            line.ShouldNotBeNull();
            using var ready = JsonDocument.Parse(line);
            ready.RootElement.GetProperty("kind").GetString().ShouldBe("ready");
            using var client = new CodeAssistClient(new Uri(ready.RootElement.GetProperty("endpoint").GetString()!), token);
            await client.CheckAsync(deadline.Token);

            process.StandardInput.Close();

            if (managed)
            {
                await process.WaitForExitAsync(deadline.Token);
                process.ExitCode.ShouldBe(0);
                (await errors).ShouldNotContain("Server startup or execution failed");
            }
            else
            {
                await Should.ThrowAsync<TimeoutException>(() => process.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(1), deadline.Token));
                await client.CheckAsync(deadline.Token);
                process.HasExited.ShouldBeFalse();
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Should_StopManagedServer_When_CommandLifetimeIsCanceled()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var environment = new Dictionary<string, string>
        {
            [SettingsLocations.HomeVariable] = _cli.Files.Locations.Home,
            [SettingsLocations.ManagedVariable] = _cli.Files.Locations.ManagedDirectory,
            ["Host__RecoverConversations"] = "false",
            ["ENABLED_LIVE_TESTS"] = "0",
        };
        await using var server = await ManagedServer.StartAsync(
            Path.Combine(AppContext.BaseDirectory, "service", "CodeAssist.Server.dll"),
            _cli.Files.Locations.WorkingDirectory, environment, cancellation: lifetime.Token, token: new string('t', 40));
        await server.Client.CheckAsync(deadline.Token);
        using var observer = new CodeAssistClient(server.Endpoint, new string('t', 40));

        await lifetime.CancelAsync();

        await server.WaitForExitAsync(deadline.Token);
        await server.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(() => server.Client.CheckAsync(deadline.Token));
        await Should.ThrowAsync<HttpRequestException>(() => observer.CheckAsync(deadline.Token));
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("HEAD", false)]
    [InlineData("HEAD", true)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", false)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", false)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", false)]
    [InlineData("DELETE", true)]
    [InlineData("OPTIONS", false)]
    [InlineData("OPTIONS", true)]
    public async Task Should_SendAuthenticatedMethodPathAndBody_When_RequestIsValid(string method, bool direct)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        const string path = "/v1/probe?limit=2&name=a%20b";
        const string payload = "{\"note\":\"[bold]literal[/]\",\"count\":2}";
        var data = method is "GET" or "HEAD" ? null : payload;
        var responseBody = method == "HEAD" ? "" : "[red]raw[/]\nsecond line";
        api.Raw(method, "/v1/probe", responseBody, "text/plain", 202);

        var response = await SendRequestAsync(api, direct, method, path, data);

        response.ShouldBe(new ApiResponse(202, "text/plain", responseBody));
        var request = api.RequestAt("/v1/probe");
        request.Method.ToUpperInvariant().ShouldBe(method);
        new Uri(request.Url).PathAndQuery.ShouldBe(path);
        request.Headers!["Authorization"].Single().ShouldBe("Bearer " + _cli.Environment["Host__Listen__Http__Token"]);
        if (data is null) request.Body.ShouldBeNullOrEmpty();
        else
        {
            request.Body.ShouldBe(payload);
            request.Headers["Content-Type"].Single().ShouldStartWith("application/json");
        }
        api.Calls.ShouldBe(direct ? [$"{method} /v1/probe"] : ["GET /v1/health", $"{method} /v1/probe"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReadSchemaFromSelectedService_When_OpenApiPathIsRequested(bool direct)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        const string schema = "{\"openapi\":\"3.1.0\",\"paths\":{}}";
        api.Raw("GET", "/openapi/v1.json", schema);

        var response = await SendRequestAsync(api, direct, "GET", "/openapi/v1.json");

        response.ShouldBe(new ApiResponse(200, "application/json", schema));
        api.RequestAt("/openapi/v1.json").Headers!["Authorization"].Single().ShouldBe("Bearer " + _cli.Environment["Host__Listen__Http__Token"]);
        api.Calls.ShouldBe(direct ? ["GET /openapi/v1.json"] : ["GET /v1/health", "GET /openapi/v1.json"]);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("authority")]
    [InlineData("backslash")]
    [InlineData("control")]
    [InlineData("tab")]
    [InlineData("nul")]
    [InlineData("delete-control")]
    [InlineData("hash")]
    [InlineData("prefix")]
    public async Task Should_RejectUnsafePathWithoutHttp_When_RequestLeavesSelectedApi(string partition)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        using var destination = new ConversationCommandTests.ConversationApi(_cli);
        var path = partition switch
        {
            "origin" => destination.Url + "/v1/probe",
            "authority" => "//" + new Uri(destination.Url).Authority + "/v1/probe",
            "backslash" => "/v1/\\probe",
            "control" => "/v1/probe\r\nInjected: value",
            "tab" => "/v1/probe\tvalue",
            "nul" => "/v1/probe\0value",
            "delete-control" => "/v1/probe\u007fvalue",
            "hash" => "/v1/probe#secret",
            _ => "/v10/probe",
        };
        using var client = api.CreateClient();

        var error = await Should.ThrowAsync<ArgumentException>(() => client.RequestAsync("GET", path, cancellation: TestContext.Current.CancellationToken));
        var result = await _cli.Run("request", "GET", path, "--endpoint", api.Url);

        error.ParamName.ShouldBe("path");
        error.Message.ShouldStartWith("Use an API path on the selected service.");
        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain("Use a service-relative API path.");
        api.Calls.ShouldBeEmpty();
        destination.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("method")]
    [InlineData("get-body")]
    [InlineData("head-body")]
    [InlineData("malformed-json")]
    public async Task Should_RejectInvalidMethodOrDataBeforeHttp_When_RequestArgumentsConflict(string partition)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        var method = partition switch { "method" => "TRACE", "get-body" => "GET", "head-body" => "HEAD", _ => "POST" };
        string[] data = partition == "method" ? [] : ["--data", partition == "malformed-json" ? "{" : "null"];

        var result = await _cli.Run(["request", method, "/v1/probe", "--endpoint", api.Url, .. data]);

        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain(partition switch
        {
            "method" => "Unsupported HTTP method.",
            "malformed-json" => "--data must contain valid JSON.",
            _ => "GET/HEAD do not accept --data.",
        });
        if (partition != "malformed-json")
        {
            using var client = api.CreateClient();
            JsonElement? body = partition == "method" ? null : JsonSerializer.Deserialize<JsonElement>("null");
            var error = await Should.ThrowAsync<ArgumentException>(() => client.RequestAsync(method, "/v1/probe", body, TestContext.Current.CancellationToken));
            error.ParamName.ShouldBe(partition == "method" ? "method" : "body");
        }
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(393215, false)]
    [InlineData(393216, false)]
    [InlineData(393217, false)]
    [InlineData(393215, true)]
    [InlineData(393216, true)]
    [InlineData(393217, true)]
    public async Task Should_EnforceRequestByteBudget_When_Utf8BodyReachesBoundary(int bytes, bool direct)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        api.Raw("POST", "/v1/probe", "accepted", "text/plain");
        var value = new string('\u00e9', 196606) + new string('x', bytes - 393214);
        var data = "\"" + value + "\"";
        System.Text.Encoding.UTF8.GetByteCount(data).ShouldBe(bytes);

        if (bytes <= 393216)
        {
            (await SendRequestAsync(api, direct, "POST", "/v1/probe", data)).ShouldBe(new ApiResponse(200, "text/plain", "accepted"));
            api.Body<string>("/v1/probe").ShouldBe(value);
        }
        else
        {
            if (direct)
            {
                using var client = api.CreateClient();
                var error = await Should.ThrowAsync<ArgumentException>(() => client.RequestAsync("POST", "/v1/probe", JsonSerializer.Deserialize<JsonElement>(data), TestContext.Current.CancellationToken));
                error.ParamName.ShouldBe("body");
                error.Message.ShouldStartWith("Request body exceeds 384 KiB.");
            }
            else
            {
                var result = await _cli.Run("request", "POST", "/v1/probe", "--data", data, "--endpoint", api.Url);
                result.Code.ShouldBe(ExitCodes.UsageOrConfig);
                result.Output.ShouldContain("Request body exceeds 384 KiB.");
            }
            api.Calls.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Should_ReportStatusWithoutBodyOrCredentials_When_RequestFails(int status)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        const string secret = "PRIVATE_UPSTREAM_BODY";
        api.Raw("POST", "/v1/probe", secret + _cli.Environment["Host__Listen__Http__Token"], status: status);
        using var client = api.CreateClient();

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.RequestAsync("POST", "/v1/probe", cancellation: TestContext.Current.CancellationToken));
        var result = await _cli.Run("request", "POST", "/v1/probe", "--endpoint", api.Url);

        error.StatusCode.ShouldBe((System.Net.HttpStatusCode)status);
        error.Message.ShouldBe($"Server request failed ({status}).");
        result.Code.ShouldBe(ExitCodes.Failure);
        result.Output.ShouldContain(error.Message);
        result.Output.ShouldNotContain(secret);
        error.ToString().ShouldNotContain(secret);
        api.ShouldNotLeakToken(result.Output + error);
        api.Calls.ShouldBe(["POST /v1/probe", "GET /v1/health", "POST /v1/probe"]);
    }

    [Theory]
    [InlineData(8388607, false)]
    [InlineData(8388608, false)]
    [InlineData(8388609, false)]
    [InlineData(8388607, true)]
    [InlineData(8388608, true)]
    [InlineData(8388609, true)]
    public async Task Should_EnforceResponseByteBudget_When_Utf8ResponseReachesBoundary(int bytes, bool direct)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        var body = new string('\u00e9', bytes / 2) + (bytes % 2 == 1 ? "x" : "");
        api.Raw("GET", "/v1/probe", body, "text/plain");
        if (bytes <= 8388608)
        {
            if (direct)
            {
                using var client = api.CreateClient();
                (await client.RequestAsync("GET", "/v1/probe", cancellation: TestContext.Current.CancellationToken)).ShouldBe(new ApiResponse(200, "text/plain", body));
            }
            else
            {
                var result = await _cli.Run("request", "GET", "/v1/probe", "--endpoint", api.Url);
                result.Code.ShouldBe(ExitCodes.Success, result.Output);
                result.Output.ShouldBe(body + Environment.NewLine);
            }
        }
        else
        {
            if (direct)
            {
                using var client = api.CreateClient();
                var error = await Should.ThrowAsync<InvalidDataException>(() => client.RequestAsync("GET", "/v1/probe", cancellation: TestContext.Current.CancellationToken));
                error.Message.ShouldBe("Server response exceeds its budget.");
            }
            else
            {
                var result = await _cli.Run("request", "GET", "/v1/probe", "--endpoint", api.Url);
                result.Code.ShouldBe(ExitCodes.Failure);
                result.Output.ShouldContain("Server response exceeds its budget.");
                result.Output.ShouldNotContain(body[..100]);
            }
        }
        api.Calls.ShouldBe(direct ? ["GET /v1/probe"] : ["GET /v1/health", "GET /v1/probe"]);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Should_RefuseRedirectWithoutForwardingCredentials_When_ServerReturnsLocation(int status)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        using var destination = new ConversationCommandTests.ConversationApi(_cli);
        api.Raw("GET", "/v1/probe", "PRIVATE_REDIRECT_BODY", status: status, location: destination.Url + "/v1/sessions");
        using var client = api.CreateClient();

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.RequestAsync("GET", "/v1/probe", cancellation: TestContext.Current.CancellationToken));
        var result = await _cli.Run("request", "GET", "/v1/probe", "--endpoint", api.Url);

        error.StatusCode.ShouldBe((System.Net.HttpStatusCode)status);
        error.Message.ShouldBe($"Server request failed ({status}).");
        result.Code.ShouldBe(ExitCodes.Failure);
        result.Output.ShouldContain(error.Message);
        result.Output.ShouldNotContain("PRIVATE_REDIRECT_BODY");
        destination.Calls.ShouldBeEmpty();
        api.Calls.ShouldBe(["GET /v1/probe", "GET /v1/health", "GET /v1/probe"]);
        api.ShouldNotLeakToken(result.Output);
    }

    [Fact]
    public async Task Should_RefuseSseWithoutPrintingEvents_When_RequestTargetsStream()
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        api.Raw("POST", "/v1/probe", "data: PRIVATE_STREAM_BODY\n\n", "text/event-stream; charset=utf-8");
        using var client = api.CreateClient();

        var error = await Should.ThrowAsync<InvalidDataException>(() => client.RequestAsync("POST", "/v1/probe", cancellation: TestContext.Current.CancellationToken));
        var result = await _cli.Run("request", "POST", "/v1/probe", "--endpoint", api.Url);

        error.Message.ShouldBe("Use run/chat for streaming operations.");
        result.Code.ShouldBe(ExitCodes.Failure);
        result.Output.ShouldContain(error.Message);
        result.Output.ShouldNotContain("PRIVATE_STREAM_BODY");
        api.Calls.ShouldBe(["POST /v1/probe", "GET /v1/health", "POST /v1/probe"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReturnEmptyBodyAndStatus_When_ServerRespondsWithNoContent(bool json)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        api.Raw("DELETE", "/v1/probe", "", "text/plain", 204);
        using var client = api.CreateClient();

        var response = await client.RequestAsync("DELETE", "/v1/probe", cancellation: TestContext.Current.CancellationToken);
        string[] format = json ? ["--json"] : [];
        var result = await _cli.Run(["request", "DELETE", "/v1/probe", "--endpoint", api.Url, .. format]);

        response.ShouldBe(new ApiResponse(204, "text/plain", ""));
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        if (json) JsonSerializer.Deserialize<ApiResponse>(result.Output, JsonSerializerOptions.Web).ShouldBe(response);
        else result.Output.ShouldBe(Environment.NewLine);
        api.Calls.ShouldBe(["DELETE /v1/probe", "GET /v1/health", "DELETE /v1/probe"]);
    }

    [Fact]
    public async Task Should_ExposeOnlySortedSafeRoleMetadata_When_InspectingRolesOffline()
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        _cli.Environment["Providers__default__Endpoint"] = api.Url + "/v1/";
        _cli.Environment["Providers__default__ApiKey"] = "PRIVATE_PROVIDER_KEY";
        _cli.Environment["Agent__Model"] = "absent/model";
        await File.WriteAllTextAsync(_cli.UserFile, """
            {"roles":{"zulu":{"prompt":"PRIVATE_SYSTEM_PROMPT"},"alpha":{"description":"Review only","model":"default/review","smallModel":"default/small","hidden":true,"delegatable":true,"maxTurns":7,"prompt":"PRIVATE_ROLE_PROMPT","color":"red","tools":{"allow":["read","search"],"deny":["exec"]}}}}
            """, TestContext.Current.CancellationToken);
        var files = Directory.GetFiles(_cli.Files.Root, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = await _cli.Run("inspect", "roles", "--json");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var roles = JsonSerializer.Deserialize<JsonElement>(result.Output).EnumerateArray().ToArray();
        roles.Select(role => role.GetProperty("id").GetString()).ShouldBe(["alpha", "builder", "planner", "reviewer", "zulu"]);
        foreach (var role in roles)
        {
            string[] properties = role.GetProperty("id").GetString() switch
            {
                "alpha" => ["id", "description", "model", "smallModel", "hidden", "delegatable", "maxTurns", "allow", "deny"],
                "zulu" => ["id", "hidden", "delegatable", "allow", "deny"],
                _ => ["id", "description", "hidden", "delegatable", "allow", "deny"],
            };
            role.EnumerateObject().Select(property => property.Name).Order().ShouldBe(properties.Order());
        }
        var alpha = roles[0];
        alpha.GetProperty("description").GetString().ShouldBe("Review only");
        alpha.GetProperty("model").GetString().ShouldBe("default/review");
        alpha.GetProperty("smallModel").GetString().ShouldBe("default/small");
        alpha.GetProperty("hidden").GetBoolean().ShouldBeTrue();
        alpha.GetProperty("delegatable").GetBoolean().ShouldBeTrue();
        alpha.GetProperty("maxTurns").GetInt32().ShouldBe(7);
        alpha.GetProperty("allow").Deserialize<string[]>().ShouldBe(["read", "search"]);
        alpha.GetProperty("deny").Deserialize<string[]>().ShouldBe(["exec"]);
        roles[^1].GetProperty("hidden").GetBoolean().ShouldBeFalse();
        roles[^1].GetProperty("delegatable").GetBoolean().ShouldBeFalse();
        result.Output.ShouldNotContain("PRIVATE_");
        api.ShouldNotLeakToken(result.Output);
        api.Calls.ShouldBeEmpty();
        Directory.GetFiles(_cli.Files.Root, "*", SearchOption.AllDirectories).Order().ShouldBe(files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReportLocationsWithoutServiceOrNetwork_When_InspectingPathsWithInvalidSettings(bool broken)
    {
        using var api = new ConversationCommandTests.ConversationApi(_cli);
        _cli.Environment["Providers__default__Endpoint"] = api.Url + "/v1/";
        _cli.Environment["Agent__Model"] = "absent/model";
        await File.WriteAllTextAsync(_cli.UserFile, broken ? "{broken" : "{}", TestContext.Current.CancellationToken);
        var files = Directory.GetFiles(_cli.Files.Root, "*", SearchOption.AllDirectories).Order().ToArray();

        var result = await _cli.Run("inspect", "paths");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var paths = JsonSerializer.Deserialize<JsonElement>(result.Output);
        paths.GetProperty("home").GetString().ShouldBe(_cli.Files.Home);
        paths.GetProperty("workingDirectory").GetString().ShouldBe(_cli.Files.Locations.WorkingDirectory);
        paths.GetProperty("managedDirectory").GetString().ShouldBe(_cli.Files.Managed);
        paths.GetProperty("stateDirectory").GetString().ShouldBe(_cli.Files.Locations.StateDirectory);
        paths.GetProperty("userFile").GetString().ShouldBe(_cli.UserFile);
        paths.GetProperty("managedFile").GetString().ShouldBe(_cli.Files.Locations.ManagedFile);
        paths.GetProperty("preferencesFile").GetString().ShouldBe(_cli.Files.Locations.PreferencesFile);
        paths.GetProperty("projectFiles").Deserialize<string[]>().ShouldBe(_cli.Files.Locations.ProjectFiles());
        paths.GetProperty("executableDirectory").GetString().ShouldBe(AppContext.BaseDirectory);
        api.Calls.ShouldBeEmpty();
        Directory.GetFiles(_cli.Files.Root, "*", SearchOption.AllDirectories).Order().ShouldBe(files);
    }

    private async Task<ApiResponse> SendRequestAsync(ConversationCommandTests.ConversationApi api, bool direct, string method, string path, string? data = null)
    {
        if (direct)
        {
            using var client = api.CreateClient();
            return await client.RequestAsync(method, path, data is null ? null : JsonSerializer.Deserialize<JsonElement>(data), TestContext.Current.CancellationToken);
        }
        string[] body = data is null ? [] : ["--data", data];
        var result = await _cli.Run(["request", method.ToLowerInvariant(), path, "--endpoint", api.Url, "--json", .. body]);
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        api.ShouldNotLeakToken(result.Output);
        return JsonSerializer.Deserialize<ApiResponse>(result.Output, JsonSerializerOptions.Web)!;
    }

    [Fact]
    public async Task ConfigSetThenGetRoundTrips()
    {
        var (setCode, setOutput) = await _cli.Run("config", "set", "agent.maxTurns", "12");
        var (getCode, getOutput) = await _cli.Run("config", "get", "agent.maxTurns");

        setCode.ShouldBe(0, setOutput);
        getCode.ShouldBe(0);
        getOutput.Trim().ShouldBe("12");
        _cli.UserJson()["agent"]!["maxTurns"]!.GetValue<int>().ShouldBe(12);
    }

    [Fact]
    public async Task InvalidSetFailsWithUsageCodeAndLeavesFileAlone()
    {
        var (code, output) = await _cli.Run("config", "set", "agent.maxTurns", "99999");

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("outside");
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task FlagsOverrideForOneRunOnly()
    {
        await _cli.Run("config", "set", "agent.maxTurns", "12");

        var (_, withFlag) = await _cli.Run("config", "get", "agent.maxTurns", "--max-turns", "3");
        var (_, viaSet) = await _cli.Run("config", "get", "agent.maxTurns", "--set", "agent.maxTurns=4");
        var (_, without) = await _cli.Run("config", "get", "agent.maxTurns");

        withFlag.Trim().ShouldBe("3");
        viaSet.Trim().ShouldBe("4");
        without.Trim().ShouldBe("12");
    }

    [Fact]
    public async Task InvalidFlagValueIsRejectedBeforeTheCommandRuns()
    {
        var (code, output) = await _cli.Run("config", "get", "agent.maxTurns", "--set", "agent.permissionPreset=bogus");

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("invalid settings");
    }

    [Fact]
    public async Task EnvironmentSecretsAreNeverPrinted()
    {
        _cli.Environment["Providers__default__ApiKey"] = "sk-very-secret";

        var (code, output) = await _cli.Run("config", "show", "--section", "providers", "--json");
        var (_, listOutput) = await _cli.Run("providers", "show", "default");

        code.ShouldBe(0);
        output.ShouldNotContain("sk-very-secret");
        output.ShouldContain("(secret)");
        listOutput.ShouldNotContain("sk-very-secret");
    }

    [Fact]
    public async Task ProvidersAddWritesPresetAndReportsReadiness()
    {
        var (code, output) = await _cli.Run("providers", "add", "ollama", "--default-model", "llama3.2");

        code.ShouldBe(0, output);
        var user = _cli.UserJson();
        user["providers"]!["ollama"]!["endpoint"]!.GetValue<string>().ShouldBe("http://localhost:11434/v1/");
        user["agent"]!["model"]!.GetValue<string>().ShouldBe("ollama/llama3.2");

        var (_, list) = await _cli.Run("providers", "list", "--json");
        var rows = JsonNode.Parse(list)!.AsArray();
        rows.Single(r => r!["id"]!.GetValue<string>() == "ollama")!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task ProvidersAddNeedsEndpointForAzureAndRefusesDuplicates()
    {
        var (azureCode, azureOutput) = await _cli.Run("providers", "add", "azure");
        azureCode.ShouldBe(ExitCodes.UsageOrConfig);
        azureOutput.ShouldContain("--endpoint");

        (await _cli.Run("providers", "add", "ollama")).Code.ShouldBe(0);
        var (duplicate, _) = await _cli.Run("providers", "add", "ollama");
        duplicate.ShouldBe(ExitCodes.UsageOrConfig);
        (await _cli.Run("providers", "add", "ollama", "--force")).Code.ShouldBe(0);
    }

    [Fact]
    public async Task ProvidersRemoveDeletesTheEntry()
    {
        await _cli.Run("providers", "add", "ollama");

        var (code, _) = await _cli.Run("providers", "remove", "ollama");

        code.ShouldBe(0);
        _cli.UserJson()["providers"].ShouldBeNull();
    }

    [Fact]
    public async Task ConnectorsAddStoresCommandArgumentsAndEnvironment()
    {
        var (code, output) = await _cli.Run("connectors", "add", "files", "--env", "ROOT=/data", "--", "npx", "-y", "some-server");

        code.ShouldBe(0, output);
        var server = _cli.UserJson()["connectors"]!["servers"]!["files"]!;
        server["command"]!.GetValue<string>().ShouldBe("npx");
        server["arguments"]!.AsArray().Select(a => a!.GetValue<string>()).ShouldBe(["-y", "some-server"]);
        server["environment"]!["ROOT"]!.GetValue<string>().ShouldBe("/data");

        var (_, list) = await _cli.Run("connectors", "list", "--json");
        JsonNode.Parse(list)!.AsArray().Count.ShouldBe(1);

        (await _cli.Run("connectors", "disable", "files")).Code.ShouldBe(0);
        _cli.UserJson()["connectors"]!["servers"]!["files"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();

        (await _cli.Run("connectors", "remove", "files")).Code.ShouldBe(0);
    }

    [Fact]
    public async Task ConnectorsAddRequiresCommandOrUrlButNotBoth()
    {
        (await _cli.Run("connectors", "add", "x")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        (await _cli.Run("connectors", "add", "x", "--url", "https://x.example/", "--", "npx")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        (await _cli.Run("connectors", "add", "x", "--url", "http://remote.example/")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        File.Exists(_cli.UserFile).ShouldBeFalse();
    }

    [Fact]
    public async Task BrokenSettingsBlockNormalCommandsButNotRepairOnes()
    {
        Directory.CreateDirectory(_cli.Files.Home);
        File.WriteAllText(_cli.UserFile, "{ broken");

        (await _cli.Run("providers", "list")).Code.ShouldBe(ExitCodes.UsageOrConfig);
        var (validate, output) = await _cli.Run("config", "validate");
        validate.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("Invalid JSON");
        (await _cli.Run("config", "path")).Code.ShouldBe(0);
    }

    [Fact]
    public async Task SchemaCommandPrintsParseableJsonSchema()
    {
        var (code, output) = await _cli.Run("config", "schema");

        code.ShouldBe(0);
        JsonNode.Parse(output)!["properties"]!["agent"].ShouldNotBeNull();
    }

    [Fact]
    public async Task ExplainDescribesTrustAndProvenance()
    {
        _cli.Environment["Agent__MaxTurns"] = "7";

        var (code, output) = await _cli.Run("config", "explain", "agent.maxTurns");

        code.ShouldBe(0);
        output.ShouldContain("Agent__MaxTurns");
        output.ShouldContain("stricter");
        output.ShouldContain("environment");
    }

    [Fact]
    public async Task DoctorFailsWhenTheDefaultProviderHasNoKey()
    {
        var (code, output) = await _cli.Run("doctor");

        code.ShouldBe(ExitCodes.Failure);
        output.ShouldContain("Default model");

        _cli.Environment["Providers__default__ApiKey"] = "x";
        (await _cli.Run("doctor")).Code.ShouldBe(0);
    }
}
