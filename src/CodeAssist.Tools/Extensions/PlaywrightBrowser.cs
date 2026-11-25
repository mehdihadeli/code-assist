using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;
using Microsoft.Playwright;

namespace CodeAssist.Tools.Extensions;

public sealed class PlaywrightBrowser : IBrowserAutomation, IAsyncDisposable
{
    private sealed class Session(IBrowserContext browser, ToolContext context)
    {
        public IBrowserContext Browser { get; } = browser;
        public ToolContext Context { get; set; } = context;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, IPage> Pages { get; } = [];
        public ConcurrentQueue<string> Console { get; } = new();
        public ConcurrentQueue<string> Network { get; } = new();
        public ConcurrentDictionary<string, IRequest> Requests { get; } = new();
        public ConcurrentDictionary<string, IResponse> Responses { get; } = new();
        public Dictionary<string, ICDPSession> Profilers { get; } = [];
        public ConcurrentDictionary<string, IDialog> Dialogs { get; } = new();
        public TaskCompletionSource<string> DialogOpened { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ToolResult>? Suspended { get; set; }
    }

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IToolAuthorization _authorization;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly SemaphoreSlim _creation = new(1, 1);
    private readonly HttpClient _public = new(NetworkGuard.CreateHandler(false));
    private readonly HttpClient _private = new(NetworkGuard.CreateHandler(true));
    private const int TransferLimit = 5 * 1024 * 1024;

    private sealed class HeapSnapshotBuffer
    {
        private readonly Lock _gate = new();
        private readonly List<byte[]> _chunks = [];
        private int _length;
        private bool _closed;
        private bool _overBudget;

        public void Add(JsonElement? data)
        {
            if (data is not { } json) return;
            lock (_gate)
            {
                if (_closed || _overBudget) return;
                var bytes = Encoding.UTF8.GetBytes(json.GetProperty("chunk").GetString() ?? "");
                if (_length + bytes.Length > TransferLimit) { _overBudget = true; _chunks.Clear(); return; }
                _chunks.Add(bytes);
                _length += bytes.Length;
            }
        }

        public byte[]? Complete()
        {
            lock (_gate)
            {
                _closed = true;
                if (_overBudget) return null;
                var result = new byte[_length];
                var offset = 0;
                foreach (var chunk in _chunks) { Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length); offset += chunk.Length; }
                _chunks.Clear();
                return result;
            }
        }
    }

    private PlaywrightBrowser(IPlaywright playwright, IBrowser browser, IToolAuthorization authorization)
    { _playwright = playwright; _browser = browser; _authorization = authorization; }

    public static async Task<PlaywrightBrowser> CreateAsync(IToolAuthorization authorization, string? channel = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = channel, Args = ["--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"] }).WaitAsync(cancellationToken);
            return new(playwright, browser, authorization);
        }
        catch { playwright.Dispose(); throw; }
    }

    public IReadOnlyList<BrowserOperation> Operations { get; } = [
        new("tabs", "open", ToolRisk.Execute, [], false), new("tabs", "list", ToolRisk.Read, [], false),
        new("tabs", "focus", ToolRisk.Execute, []), new("tabs", "close", ToolRisk.Execute, []),
        new("page", "navigate", ToolRisk.Network, ["url"]), new("page", "back", ToolRisk.Network, []), new("page", "forward", ToolRisk.Network, []),
        new("page", "reload", ToolRisk.Network, []), new("page", "wait", ToolRisk.Read, ["selector"]), new("page", "snapshot", ToolRisk.Read, []),
        new("page", "find", ToolRisk.Read, ["selector"]), new("page", "evaluate", ToolRisk.Execute, ["script"]),
        new("input", "click", ToolRisk.Execute, ["selector"]), new("input", "hover", ToolRisk.Execute, ["selector"]),
        new("input", "drag", ToolRisk.Execute, ["selector", "target"]), new("input", "fill", ToolRisk.Execute, ["selector", "text"]),
        new("input", "select", ToolRisk.Execute, ["selector", "text"]), new("input", "check", ToolRisk.Execute, ["selector"]),
        new("input", "key", ToolRisk.Execute, ["key"]), new("input", "scroll", ToolRisk.Execute, []), new("input", "dialog", ToolRisk.Execute, []),
        new("capture", "screenshot", ToolRisk.Read, [], FileEffect: ToolEffectKind.Write),
        new("files", "upload", ToolRisk.Execute, ["selector", "path"], FileEffect: ToolEffectKind.Read),
        new("files", "download", ToolRisk.Execute, ["selector", "path"], FileEffect: ToolEffectKind.Write),
        new("inspect", "console", ToolRisk.Read, []), new("inspect", "network", ToolRisk.Read, []),
        new("inspect", "request", ToolRisk.Read, ["id"]), new("inspect", "response", ToolRisk.Read, ["id"]), new("inspect", "metrics", ToolRisk.Read, []),
        new("inspect", "trace-start", ToolRisk.Execute, []), new("inspect", "trace-stop", ToolRisk.Execute, ["path"], FileEffect: ToolEffectKind.Write),
        new("inspect", "cpu-start", ToolRisk.Execute, []), new("inspect", "cpu-stop", ToolRisk.Execute, ["path"], FileEffect: ToolEffectKind.Write),
        new("inspect", "heap", ToolRisk.Execute, ["path"], FileEffect: ToolEffectKind.Write),
    ];

    private async Task<Session> SessionAsync(ToolContext context, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(context.SessionId, out var existing)) return existing;
        await _creation.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(context.SessionId, out existing)) return existing;
            var browser = await _browser.NewContextAsync(new() { AcceptDownloads = true, ServiceWorkers = ServiceWorkerPolicy.Block });
            browser.SetDefaultTimeout(15000);
            var session = new Session(browser, context);
            await browser.RouteAsync("**/*", route => RouteAsync(route, session));
            await browser.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
            _sessions[context.SessionId] = session;
            return session;
        }
        finally { _creation.Release(); }
    }

    private async Task RouteAsync(IRoute route, Session session)
    {
        try
        {
            var context = session.Context;
            var uri = new Uri(route.Request.Url);
            if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) { await route.AbortAsync(); return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var allowed = await _authorization.AuthorizeAsync(new("browser.request", "Browser network request", "{}", ToolRisk.Network, ["net"]),
                new([new(ToolEffectKind.Net, uri.AbsoluteUri)]), context, timeout.Token);
            if (!allowed.Allowed) { await route.AbortAsync(); return; }
            using var request = new HttpRequestMessage(new HttpMethod(route.Request.Method), uri);
            if (route.Request.PostDataBuffer is { } body) request.Content = new ByteArrayContent(body);
            foreach (var header in await route.Request.AllHeadersAsync())
            {
                if (header.Key.Equals("host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var response = await (context.Limits.AllowPrivateHosts ? _private : _public).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Content.Headers.ContentLength > TransferLimit) { await route.AbortAsync(); return; }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + count > TransferLimit) { await route.AbortAsync(); return; }
                memory.Write(buffer, 0, count);
            }
            var headers = response.Headers.Concat(response.Content.Headers).Where(header => !header.Key.Equals("content-encoding", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = headers, BodyBytes = memory.ToArray() });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { try { await route.AbortAsync(); } catch (PlaywrightException) { } }
    }

    public async Task<ToolResult> ExecuteAsync(BrowserCall call, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.SessionId == Guid.Empty) throw new ToolArgumentException("Browser calls require a real session ID.");
        var operation = Operations.FirstOrDefault(item => $"{item.Family}.{item.Name}" == call.Operation) ?? throw new ToolArgumentException("Unsupported browser operation.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(context.Limits.ProcessTimeout);
        var session = await SessionAsync(context, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (session.Suspended is not null && call.Operation is not ("input.dialog" or "tabs.close")) return ToolResult.Error("A dialog is pending; answer it before another page operation.");
            session.Context = context;
            session.DialogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = RunAsync(call, operation, session, context, deadline.Token);
            try
            {
                var completed = await Task.WhenAny(work, session.DialogOpened.Task).WaitAsync(deadline.Token);
                if (completed != work && !work.IsCompleted)
                {
                    session.Suspended = work;
                    return Text(new { dialogPending = true, tabId = await session.DialogOpened.Task, untrusted = true }, context);
                }
                var result = await work;
                if (session.Suspended is { } suspended && call.Operation is "input.dialog" or "tabs.close")
                {
                    session.Suspended = null;
                    try { await suspended.WaitAsync(deadline.Token); } catch (PlaywrightException) when (call.Operation == "tabs.close") { }
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                _sessions.TryRemove(context.SessionId, out _);
                await session.Browser.CloseAsync();
                try { await work; } catch (PlaywrightException) { }
                if (!cancellationToken.IsCancellationRequested) return ToolResult.Error("Browser operation exceeded its timeout; isolated context was closed.");
                throw;
            }
        }
        finally { session.Gate.Release(); }
    }

    private async Task<ToolResult> RunAsync(BrowserCall call, BrowserOperation operation, Session session, ToolContext context, CancellationToken cancellationToken)
    {
        var args = call.Arguments;
        if (call.Operation == "tabs.list") return Text(session.Pages.Select(pair => new { tabId = pair.Key, url = pair.Value.Url }), context);
        IPage page;
        string tab;
        if (call.Operation == "tabs.open")
        {
            if (session.Pages.Count >= 20) return ToolResult.Error("Session tab limit reached.");
            page = await session.Browser.NewPageAsync();
            tab = Guid.NewGuid().ToString("N");
            session.Pages.Add(tab, page);
            page.Console += (sender, message) => { session.Console.Enqueue(message.Text); while (session.Console.Count > 200) session.Console.TryDequeue(out _); };
            page.Dialog += (_, dialog) => { session.Dialogs[tab] = dialog; session.DialogOpened.TrySetResult(tab); };
            page.Request += (sender, request) =>
            {
                var id = Guid.NewGuid().ToString("N");
                session.Requests[id] = request;
                session.Network.Enqueue(JsonSerializer.Serialize(new { id, request.Method, request.Url }));
                while (session.Network.Count > 200) session.Network.TryDequeue(out _);
                if (session.Requests.Count > 200) session.Requests.TryRemove(session.Requests.Keys.First(), out _);
            };
            page.Response += (sender, response) =>
            {
                var id = session.Requests.FirstOrDefault(pair => ReferenceEquals(pair.Value, response.Request)).Key;
                if (id is not null) session.Responses[id] = response;
                if (session.Responses.Count > 200) session.Responses.TryRemove(session.Responses.Keys.First(), out _);
            };
            if (args.String("url") is { } url) await page.GotoAsync(url);
            return Text(new { tabId = tab, url = page.Url }, context);
        }
        if (call.TabId is null || !session.Pages.TryGetValue(call.TabId, out page!)) return ToolResult.Error("Tab not found in this session.");
        tab = call.TabId;
        var selector = args.String("selector");
        var locator = selector is null ? null : page.Locator(selector);
        var path = args.String("path") is { } raw ? new PathGuard(context.Workspace, context.ProtectedGlobs).Resolve(raw).Full : null;
        if (operation.FileEffect == ToolEffectKind.Write && path is not null && File.Exists(path)) return ToolResult.Error("Capture destination exists; choose a new path.");
        switch (call.Operation)
        {
            case "tabs.focus": await page.BringToFrontAsync(); break;
            case "tabs.close": await page.CloseAsync(); session.Pages.Remove(tab); break;
            case "page.navigate": await page.GotoAsync(args.RequireString("url")); break;
            case "page.back": await page.GoBackAsync(); break;
            case "page.forward": await page.GoForwardAsync(); break;
            case "page.reload": await page.ReloadAsync(); break;
            case "page.wait": await locator!.WaitForAsync(); break;
            case "page.snapshot": return Text(new { untrusted = true, snapshot = await page.Locator("body").AriaSnapshotAsync() }, context);
            case "page.find": return Text(new { untrusted = true, count = await locator!.CountAsync(), text = (await locator.AllTextContentsAsync()).Take(100) }, context);
            case "page.evaluate": return Text(new { untrusted = true, result = await page.EvaluateAsync<JsonElement>(args.RequireString("script")) }, context);
            case "input.click": await locator!.ClickAsync(); break;
            case "input.hover": await locator!.HoverAsync(); break;
            case "input.drag": await locator!.DragToAsync(page.Locator(args.RequireString("target"))); break;
            case "input.fill": await locator!.FillAsync(args.RequireString("text")); break;
            case "input.select": await locator!.SelectOptionAsync(args.RequireString("text")); break;
            case "input.check": await locator!.SetCheckedAsync(args.Bool("checked", true)); break;
            case "input.key": await page.Keyboard.PressAsync(args.RequireString("key")); break;
            case "input.scroll": await page.Mouse.WheelAsync(args.Int("x") ?? 0, args.Int("y") ?? 600); break;
            case "input.dialog":
                if (!session.Dialogs.TryRemove(tab, out var dialog)) return ToolResult.Error("No dialog pending for tab.");
                if (args.Bool("accept")) await dialog.AcceptAsync(args.String("text")); else await dialog.DismissAsync();
                break;
            case "capture.screenshot":
                var bytes = await page.ScreenshotAsync(new() { FullPage = args.Bool("fullPage") });
                if (bytes.Length > Math.Min(context.Limits.MaxAttachmentBytes, TransferLimit)) return ToolResult.Error("Screenshot exceeds media budget.");
                if (path is not null) await SaveNewAsync(path, bytes, cancellationToken);
                return new ToolResult("Screenshot (untrusted page content).", Attachments: [new("image/png", bytes)]);
            case "files.upload":
                if (path is null || !File.Exists(path) || new FileInfo(path).Length > TransferLimit) return ToolResult.Error("Upload missing or exceeds 5 MiB limit.");
                await locator!.SetInputFilesAsync(path); break;
            case "files.download":
                var download = await page.RunAndWaitForDownloadAsync(() => locator!.ClickAsync());
                await using (var stream = await download.CreateReadStreamAsync())
                {
                    using var memory = new MemoryStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        if (memory.Length + count > TransferLimit) { await download.CancelAsync(); return ToolResult.Error("Download exceeds 5 MiB limit."); }
                        memory.Write(buffer, 0, count);
                    }
                    await SaveNewAsync(path!, memory.ToArray(), cancellationToken);
                }
                break;
            case "inspect.console": return Text(new { untrusted = true, messages = session.Console.ToArray() }, context);
            case "inspect.network": return Text(new { untrusted = true, requests = session.Network.ToArray() }, context);
            case "inspect.request":
                if (!session.Requests.TryGetValue(args.RequireString("id"), out var request)) return ToolResult.Error("Request not retained.");
                return Text(new { untrusted = true, request.Method, request.Url, headers = Redact(await request.AllHeadersAsync()), body = request.PostData }, context);
            case "inspect.response":
                if (!session.Responses.TryGetValue(args.RequireString("id"), out var response)) return ToolResult.Error("Response not retained.");
                var responseBody = await response.BodyAsync();
                if (responseBody.Length > TransferLimit) return ToolResult.Error("Response exceeds diagnostic budget.");
                return Text(new { untrusted = true, response.Status, response.Url, headers = Redact(await response.AllHeadersAsync()), body = Encoding.UTF8.GetString(responseBody) }, context);
            case "inspect.trace-start": await session.Browser.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true }); break;
            case "inspect.trace-stop":
                var temporary = Path.GetTempFileName();
                try
                {
                    await session.Browser.Tracing.StopAsync(new() { Path = temporary });
                    if (new FileInfo(temporary).Length > TransferLimit) return ToolResult.Error("Trace exceeds artifact limit.");
                    await SaveNewAsync(path!, await File.ReadAllBytesAsync(temporary, cancellationToken), cancellationToken);
                }
                finally { File.Delete(temporary); }
                break;
            default: return await InspectAsync(call.Operation, page, session, tab, path, context, cancellationToken);
        }
        return Text(new { completed = call.Operation, tabId = tab, url = page.Url, path }, context);
    }

    private static Dictionary<string, string> Redact(Dictionary<string, string> headers) => headers.ToDictionary(pair => pair.Key,
        pair => pair.Key.Contains("auth", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("key", StringComparison.OrdinalIgnoreCase) || pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase) ? "[redacted]" : pair.Value);

    private static async Task<ToolResult> InspectAsync(string operation, IPage page, Session session, string tab, string? path, ToolContext context, CancellationToken cancellationToken)
    {
        if (!session.Profilers.TryGetValue(tab, out var cdp)) session.Profilers[tab] = cdp = await session.Browser.NewCDPSessionAsync(page);
        if (operation == "inspect.metrics")
        {
            await cdp.SendAsync("Performance.enable");
            return Text(new { untrusted = true, metrics = await cdp.SendAsync("Performance.getMetrics") }, context);
        }
        if (operation == "inspect.cpu-start") { await cdp.SendAsync("Profiler.enable"); await cdp.SendAsync("Profiler.start"); return ToolResult.Ok("CPU profile started."); }
        if (operation == "inspect.cpu-stop")
        {
            var profile = await cdp.SendAsync("Profiler.stop");
            var bytes = Encoding.UTF8.GetBytes(profile?.GetRawText() ?? "{}");
            if (bytes.Length > TransferLimit) return ToolResult.Error("CPU profile exceeds artifact limit.");
            await SaveNewAsync(path!, bytes, cancellationToken);
            return ToolResult.Ok($"CPU profile saved: {path}");
        }
        if (operation == "inspect.heap")
        {
            var buffer = new HeapSnapshotBuffer();
            void Chunk(object? sender, JsonElement? data) => buffer.Add(data);
            cdp.Event("HeapProfiler.addHeapSnapshotChunk").OnEvent += Chunk;
            try { await cdp.SendAsync("HeapProfiler.takeHeapSnapshot"); }
            finally { cdp.Event("HeapProfiler.addHeapSnapshotChunk").OnEvent -= Chunk; }
            var snapshot = buffer.Complete();
            if (snapshot is null) return ToolResult.Error("Heap snapshot exceeds artifact limit.");
            await SaveNewAsync(path!, snapshot, cancellationToken);
            return ToolResult.Ok($"Heap snapshot saved: {path}");
        }
        return ToolResult.Error("Inspection operation unavailable.");
    }

    private static async Task SaveNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".codeassist-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
        }
        finally { File.Delete(temporary); }
    }

    private static ToolResult Text(object value, ToolContext context)
    {
        var output = OutputBudget.Head(JsonSerializer.Serialize(value), context.Limits.OutputMaxLines, context.Limits.OutputMaxBytes);
        return ToolResult.Ok(output.Truncated ? output.Text + "\n[output truncated]" : output.Text);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values) await session.Browser.CloseAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
        _public.Dispose();
        _private.Dispose();
    }
}