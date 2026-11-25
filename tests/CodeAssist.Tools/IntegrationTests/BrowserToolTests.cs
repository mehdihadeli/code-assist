using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Tools.Extensions;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class BrowserToolTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("CODEASSIST_BROWSER_TESTS") == "1";

    [Fact(Skip = "Set CODEASSIST_BROWSER_TESTS=1 with installed Edge/Chromium.", SkipUnless = nameof(Enabled))]
    public async Task RealBrowserNavigatesInteractsCapturesAndRejectsCrossSessionTab()
    {
        var authorization = Substitute.For<IToolAuthorization>();
        authorization.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new ToolAuthorization(true));
        await using var browser = await PlaywrightBrowser.CreateAsync(authorization, OperatingSystem.IsWindows() ? "msedge" : null, TestContext.Current.CancellationToken);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task ServeAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    try
                    {
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, leaveOpen: true);
                        while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 }) { }
                        var body = Encoding.UTF8.GetBytes("<!doctype html><h1>Fixture</h1><button onclick=\"this.textContent='Clicked'\">Press</button><input id='name'>");
                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, lifetime.Token);
                        await stream.WriteAsync(body, lifetime.Token);
                    }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) { }
        }
        var server = ServeAsync();
        var root = Path.Combine(Path.GetTempPath(), "ca-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), Limits = new ToolLimits { AllowPrivateHosts = true } };
            var opened = await new BrowserTool(browser, "tabs").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "open", url = $"http://127.0.0.1:{port}/" })), context, lifetime.Token);
            opened.IsError.ShouldBeFalse(opened.Text);
            using var response = JsonDocument.Parse(opened.Text);
            var tabId = response.RootElement.GetProperty("tabId").GetString()!;
            var click = await new BrowserTool(browser, "input").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "click", tabId, selector = "button" })), context, lifetime.Token);
            click.IsError.ShouldBeFalse(click.Text);
            var snapshot = await new BrowserTool(browser, "page").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "snapshot", tabId })), context, lifetime.Token);
            snapshot.Text.ShouldContain("Clicked");
            var capture = await new BrowserTool(browser, "capture").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "screenshot", tabId })), context, lifetime.Token);
            capture.Attachments!.Single().Data.Length.ShouldBeGreaterThan(100);
            var dialog = await new BrowserTool(browser, "page").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "evaluate", tabId, script = "() => { alert('Fixture dialog'); return true; }" })), context, lifetime.Token);
            dialog.IsError.ShouldBeFalse(dialog.Text);
            dialog.Text.ShouldContain("dialogPending");
            var dismissed = await new BrowserTool(browser, "input").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "dialog", tabId, accept = false })), context, lifetime.Token);
            dismissed.IsError.ShouldBeFalse(dismissed.Text);
            var inspect = new BrowserTool(browser, "inspect");
            (await inspect.ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "cpu-start", tabId })), context, lifetime.Token)).IsError.ShouldBeFalse();
            var profile = await inspect.ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "cpu-stop", tabId, path = "cpu.json" })), context, lifetime.Token);
            profile.IsError.ShouldBeFalse(profile.Text);
            new FileInfo(Path.Combine(root, "cpu.json")).Length.ShouldBeGreaterThan(0);
            var heap = await inspect.ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "heap", tabId, path = "heap.json" })), context, lifetime.Token);
            if (heap.IsError) heap.Text.ShouldContain("exceeds artifact limit");
            else new FileInfo(Path.Combine(root, "heap.json")).Length.ShouldBeGreaterThan(0);
            authorization.AuthorizeAsync(Arg.Any<ToolSpec>(), Arg.Any<ToolPlan>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new ToolAuthorization(false, "denied"));
            var denied = await new BrowserTool(browser, "page").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "navigate", tabId, url = $"http://127.0.0.1:{port}/denied" })), context, lifetime.Token);
            denied.IsError.ShouldBeTrue();
            var other = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
            (await new BrowserTool(browser, "page").ExecuteAsync(ToolArguments.Parse(JsonSerializer.Serialize(new { operation = "snapshot", tabId })), other, lifetime.Token)).IsError.ShouldBeTrue();
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            await server;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BrowserToolsValidateOperationAndDeclareFileEffects()
    {
        var browser = Substitute.For<IBrowserAutomation>();
        browser.Operations.Returns([new BrowserOperation("files", "upload", CodeAssist.Contracts.ToolRisk.Execute, ["path", "selector"], FileEffect: ToolEffectKind.Read)]);
        var tool = new BrowserTool(browser, "files");
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var plan = await tool.PlanAsync(ToolArguments.Parse("""{"operation":"upload","tabId":"tab","selector":"input","path":"../file"}"""), context, TestContext.Current.CancellationToken);
        plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Read && effect.Outside);
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"missing"}"""), context, TestContext.Current.CancellationToken));
    }
}