using System.Net;
using System.Text;
using System.Text.Json;
using CodeAssist.Tools;
using CodeAssist.Mcp;
using CodeAssist.Server.Composition;
using CodeAssist.Configuration;
using ModelContextProtocol.Client;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class McpConnectionTests
{
    [Fact]
    public async Task Should_ReserveSlotsOnlyForStartupAndReuseReleasedBudget_When_InspectingDisconnectedSessions()
    {
        var starts = 0;
        await using var manager = new McpConnectionManager([new("fixture", (_, _) =>
        {
            starts++;
            return Task.FromException<McpConnectionLease>(new IOException("Fixture startup failure."));
        })]);
        var token = TestContext.Current.CancellationToken;
        for (var index = 0; index < 256; index++)
        {
            var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
            (await manager.StatusAsync("fixture", context, token)).Connected.ShouldBeFalse();
            await manager.DisconnectAsync("fixture", context, token);
            await Should.ThrowAsync<InvalidOperationException>(() => manager.ListAsync("fixture", context, token));
        }
        starts.ShouldBe(0);
        var sessions = Enumerable.Range(0, 128).Select(_ => new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() }).ToArray();
        foreach (var context in sessions) await Should.ThrowAsync<IOException>(() => manager.ConnectAsync("fixture", context, token));
        var next = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        await Should.ThrowAsync<InvalidOperationException>(() => manager.ConnectAsync("fixture", next, token));
        await manager.ReleaseSessionAsync(sessions[0], token);
        await Should.ThrowAsync<IOException>(() => manager.ConnectAsync("fixture", next, token));
        starts.ShouldBe(129);
    }

    [Fact]
    public async Task Should_IsolateFilterAndFreezeConnections_When_ConfiguredProfilesAreUsed()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/mcp").UsingPost()).AtPriority(10).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBody("""{"jsonrpc":"2.0","id":{{request.bodyAsJson.id}},"error":{"code":-32601,"message":"Method not found"}}""").WithTransformer());
        Reply("initialize", """{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}""");
        Reply("tools/list", """{"tools":[{"name":"echo","inputSchema":{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}},{"name":"delete","inputSchema":{"type":"object"}}]}""");
        Reply("tools/call", """{"content":[{"type":"text","text":"fixture result"}]}""");
        server.Given(Request.Create().WithPath("/mcp").UsingPost().WithBody(new JsonPartialMatcher("""{"method":"notifications/initialized"}"""))).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(202));
        var settings = new CodeAssistSettings();
        settings.Connectors.Servers["fixture"] = new()
        {
            Transport = ConnectorTransport.Http, Url = server.Url + "/mcp", FreezeCatalog = true,
            Headers = new() { ["X-Fixture"] = "{env:FIXTURE_CREDENTIAL}" },
            Tools = new() { DefaultEnabled = false, Entries = new() { ["echo"] = true } },
        };
        await using var connections = ConnectorComposition.Create(settings, Path.GetTempPath(), new Dictionary<string, string> { ["FIXTURE_CREDENTIAL"] = "fixture-reference-value" });
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var other = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        (await connections.ConnectAsync("fixture", context, TestContext.Current.CancellationToken)).Tools.ShouldBe(1);
        (await connections.StatusAsync("fixture", other, TestContext.Current.CancellationToken)).Connected.ShouldBeFalse();
        (await connections.ListAsync("fixture", context, TestContext.Current.CancellationToken)).Single().Name.ShouldBe("mcp.fixture.echo");
        await Should.ThrowAsync<UnauthorizedAccessException>(() => connections.CallAsync("fixture", "delete", ToolArguments.Parse("{}"), context, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ToolArgumentException>(() => connections.CallAsync("fixture", "echo", ToolArguments.Parse("{}"), context, TestContext.Current.CancellationToken));
        (await connections.CallAsync("fixture", "echo", ToolArguments.Parse("""{"message":"hello"}"""), context, TestContext.Current.CancellationToken)).Text.ShouldBe("fixture result");
        Reply("tools/list", """{"tools":[]}""");
        (await connections.ListAsync("fixture", context, TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        server.LogEntries.Where(entry => entry.RequestMessage?.Body?.Contains("tools/list", StringComparison.Ordinal) == true).Count().ShouldBe(1);
        await connections.DisconnectAsync("fixture", context, TestContext.Current.CancellationToken);
        (await connections.StatusAsync("fixture", context, TestContext.Current.CancellationToken)).Connected.ShouldBeFalse();
        (await connections.ConnectAsync("fixture", context, TestContext.Current.CancellationToken)).Tools.ShouldBe(0);

        void Reply(string method, string body) => server.Given(Request.Create().WithPath("/mcp").UsingPost().WithHeader("X-Fixture", "fixture-reference-value").WithBody(new JsonPartialMatcher(JsonSerializer.Serialize(new { method })))).AtPriority(1)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("{\"jsonrpc\":\"2.0\",\"id\":{{request.bodyAsJson.id}},\"result\":" + body + "}").WithTransformer());
    }

    [Fact]
    public async Task Should_ListAndRetrieveUntrustedPrompts_When_ServerAdvertisesPromptCapability()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/mcp").UsingPost()).AtPriority(10).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBody("""{"jsonrpc":"2.0","id":{{request.bodyAsJson.id}},"error":{"code":-32601,"message":"Method not found"}}""").WithTransformer());
        Reply("initialize", """{"protocolVersion":"2025-11-25","capabilities":{"prompts":{},"resources":{}},"serverInfo":{"name":"fixture","version":"1"}}""");
        Reply("prompts/list", """{"prompts":[{"name":"inspect","description":"Review input","arguments":[{"name":"target","required":true}]}]}""");
        Reply("prompts/get", """{"description":"Untrusted guidance","messages":[{"role":"user","content":{"type":"text","text":"Review supplied target"}}]}""");
        server.Given(Request.Create().WithPath("/mcp").UsingPost().WithBody(new JsonPartialMatcher("""{"method":"notifications/initialized"}"""))).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(202));
        using var http = new HttpClient();
        var transport = new HttpClientTransport(new() { Endpoint = new Uri(server.Url + "/mcp"), EnableStandaloneGetStream = false }, http);
        await using var connection = await McpToolConnection.ConnectAsync("fixture", transport, TestContext.Current.CancellationToken);
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        (await connection.ListPromptsAsync("fixture", context, TestContext.Current.CancellationToken)).Single().Arguments.Single().Required.ShouldBeTrue();
        var result = await connection.GetPromptAsync("fixture", "inspect", new Dictionary<string, string> { ["target"] = "service" }, context, TestContext.Current.CancellationToken);
        result.Messages.Single().Role.ShouldBe("user");
        result.Messages.Single().Content.Text.ShouldBe("Review supplied target");
        await Should.ThrowAsync<ArgumentException>(() => connection.GetPromptAsync("fixture", "inspect", new Dictionary<string, string>(), context, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() => connection.GetPromptAsync("fixture", "inspect", new Dictionary<string, string> { ["unknown"] = "value" }, context, TestContext.Current.CancellationToken));
        connection.Tools.ShouldBeEmpty();

        void Reply(string method, string body) => server.Given(Request.Create().WithPath("/mcp").UsingPost().WithBody(new JsonPartialMatcher(JsonSerializer.Serialize(new { method })))).AtPriority(1)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("{\"jsonrpc\":\"2.0\",\"id\":{{request.bodyAsJson.id}},\"result\":" + body + "}").WithTransformer());
    }

    [Fact]
    public async Task Should_ReplaceCatalogAtomically_When_RefreshingImportedDefinitions()
    {
        var fixture = new FixtureHandler();
        using var http = new HttpClient(fixture);
        var transport = new HttpClientTransport(new() { Endpoint = new Uri("https://fixture.invalid/mcp"), EnableStandaloneGetStream = false }, http);
        await using var connection = await McpToolConnection.ConnectAsync("fixture", transport, TestContext.Current.CancellationToken);
        var snapshot = connection.Tools;
        fixture.ToolName = "updated";
        await connection.RefreshAsync(TestContext.Current.CancellationToken);
        snapshot.Single().Spec.Name.ShouldBe("mcp.fixture.echo");
        connection.Tools.Single().Spec.Name.ShouldBe("mcp.fixture.updated");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_PreserveAndValidateRichResults_When_InvokingImportedTools(bool valid)
    {
        using var http = new HttpClient(new FixtureHandler(true));
        var transport = new HttpClientTransport(new() { Endpoint = new Uri("https://fixture.invalid/mcp"), EnableStandaloneGetStream = false }, http);
        await using var connection = await McpToolConnection.ConnectAsync("fixture", transport, TestContext.Current.CancellationToken);
        var result = await connection.Tools.Single().ExecuteAsync(ToolArguments.Parse(valid ? "{\"message\":\"mixed\"}" : "{\"message\":\"invalid\"}"), new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        result.IsError.ShouldBe(!valid, result.Text);
        if (valid)
        {
            result.StructuredContent.ShouldNotBeNull().GetProperty("answer").GetInt32().ShouldBe(42);
            result.Text.ShouldContain("embedded text");
            result.Text.ShouldContain("record:linked");
            result.Attachments.ShouldNotBeNull().Single().Data.ShouldBe(new byte[] { 1, 2, 3 });
        }
    }

    [Fact]
    public async Task OfficialClientDiscoversInvokesAndReadsResourcesThroughHttp()
    {
        using var http = new HttpClient(new FixtureHandler());
        var transport = new HttpClientTransport(new() { Endpoint = new Uri("https://fixture.invalid/mcp"), EnableStandaloneGetStream = false }, http);
        await using var connection = await McpToolConnection.ConnectAsync("fixture", transport, TestContext.Current.CancellationToken);
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var tool = connection.Tools.Single();
        tool.Spec.Name.ShouldBe("mcp.fixture.echo");
        var catalog = ToolComposition.CreateCatalog(new ToolSettings(), new(Sources: [connection], Resources: new ResourceSources([connection])));
        catalog.Find("mcp.fixture.echo").ShouldBeSameAs(tool);
        catalog.Find("resource.read").ShouldNotBeNull();
        catalog.Visible(["read"]).ShouldNotContain(tool);
        var plan = await tool.PlanAsync(ToolArguments.Parse("{}"), context, TestContext.Current.CancellationToken);
        plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Exec);
        var result = await tool.ExecuteAsync(ToolArguments.Parse("""{"message":"hello"}"""), context, TestContext.Current.CancellationToken);
        result.Text.ShouldBe("hello");
        var resources = await connection.ListAsync("fixture", context, TestContext.Current.CancellationToken);
        resources.Resources.Single().Uri.ShouldBe("record:42");
        resources.Templates.Single().UriTemplate.ShouldBe("record:{id}");
        (await connection.ReadAsync("fixture", "record:42", context, TestContext.Current.CancellationToken))!.Single().Text.ShouldBe("answer");
    }

    private sealed class FixtureHandler(bool rich = false) : HttpMessageHandler
    {
        public string ToolName { get; set; } = "echo";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post) return new(HttpStatusCode.MethodNotAllowed);
            using var message = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            object result = root.GetProperty("method").GetString() switch
            {
                "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { }, resources = new { } }, serverInfo = new { name = "fixture", version = "1" } },
                "tools/list" => new { tools = rich ? new object[] { new { name = ToolName, description = "echo", inputSchema = new { type = "object" }, outputSchema = new { type = "object", properties = new { answer = new { type = "integer" } }, required = new[] { "answer" } } } } : new object[] { new { name = ToolName, description = "echo", inputSchema = new { type = "object" } } } },
                "tools/call" when rich => new { structuredContent = root.GetProperty("params").GetProperty("arguments").GetProperty("message").GetString() == "mixed" ? JsonSerializer.SerializeToElement(new { answer = 42 }) : JsonSerializer.SerializeToElement(new { answer = "invalid" }), content = new object[] { new { type = "resource", resource = new { uri = "record:embedded", text = "embedded text" } }, new { type = "resource", resource = new { uri = "record:binary", blob = "AQID", mimeType = "application/octet-stream" } }, new { type = "resource_link", uri = "record:linked", name = "linked" } } },
                "tools/call" => new { content = new[] { new { type = "text", text = root.GetProperty("params").GetProperty("arguments").GetProperty("message").GetString() } } },
                "resources/list" => new { resources = new[] { new { uri = "record:42", name = "record" } } },
                "resources/templates/list" => new { resourceTemplates = new[] { new { uriTemplate = "record:{id}", name = "record" } } },
                "resources/read" => new { contents = new[] { new { uri = "record:42", text = "answer" } } },
                _ => null!,
            };
            if (result is null) return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Method not found" },
            }), Encoding.UTF8, "application/json") };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }), Encoding.UTF8, "application/json") };
        }
    }
}