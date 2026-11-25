using CodeAssist.Security;
using CodeAssist.Tools;
using CodeAssist.Mcp;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using NSubstitute;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class McpAuthenticationTests
{
    public static bool Windows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(Windows), Skip = "Windows user-scoped data protection is required.")]
    public async Task Should_EncryptRetainAndForgetCredentials_When_WindowsUserCacheIsUsed()
    {
        var directory = Directory.CreateTempSubdirectory("codeassist-mcp-credentials-").FullName;
        try
        {
            var cache = new ProtectedMcpTokenCache(directory, "fixture-profile");
            await cache.StoreTokensAsync(new() { AccessToken = "fixture-access-secret", RefreshToken = "fixture-refresh-secret", TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow, ClientId = "fixture-client" }, TestContext.Current.CancellationToken);
            System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Directory.GetFiles(directory).Single(), TestContext.Current.CancellationToken)).ShouldNotContain("fixture-refresh-secret");
            var restored = await new ProtectedMcpTokenCache(directory, "fixture-profile").GetTokensAsync(TestContext.Current.CancellationToken);
            restored!.RefreshToken.ShouldBe("fixture-refresh-secret");
            restored.ClientId.ShouldBe("fixture-client");
            (await new ProtectedMcpTokenCache(directory, "different-profile").GetTokensAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
            await cache.ForgetAsync(TestContext.Current.CancellationToken);
            (await cache.GetTokensAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("{\"count\":3}", "accept")]
    [InlineData("{\"count\":\"wrong\"}", "decline")]
    public async Task Should_ValidateTrustedFormResponses_When_ServerRequestsElicitation(string answer, string action)
    {
        var interaction = Substitute.For<IUserInteraction>();
        interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new[] { new QuestionAnswer([], answer) });
        var bridge = new McpElicitationBridge(interaction);
        using var request = JsonDocument.Parse("""{"mode":"form","message":"Choose count","requestedSchema":{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}}""");
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var result = await bridge.HandleAsync("fixture", request.RootElement, context, TestContext.Current.CancellationToken);
        result.GetProperty("action").GetString().ShouldBe(action);
    }

    [Fact]
    public async Task Should_RejectUnsafeInputLinksBeforeInteraction_When_ServerRequestsExternalInput()
    {
        var interaction = Substitute.For<IUserInteraction>();
        var bridge = new McpElicitationBridge(interaction, (_, _) => throw new InvalidOperationException("Must not open."));
        using var request = JsonDocument.Parse("""{"mode":"url","message":"Sign in","url":"file:///private"}""");
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        (await bridge.HandleAsync("fixture", request.RootElement, context, TestContext.Current.CancellationToken)).GetProperty("action").GetString().ShouldBe("decline");
        await interaction.DidNotReceive().AskAsync(Arg.Any<IReadOnlyList<Question>>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_ValidateOAuthStateAndIsolateConnections_When_AuthenticatingOverHttp(bool validState)
    {
        using var server = WireMockServer.Start();
        var metadata = server.Url! + "/.well-known/oauth-protected-resource";
        server.Given(Request.Create().WithPath("/mcp").UsingPost()).AtPriority(10).RespondWith(Response.Create().WithStatusCode(401).WithHeader("WWW-Authenticate", $"Bearer resource_metadata=\"{metadata}\""));
        server.Given(Request.Create().WithPath("/mcp").UsingGet()).RespondWith(Response.Create().WithStatusCode(405));
        server.Given(Request.Create().WithPath("/.well-known/oauth-protected-resource").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBodyAsJson(new { resource = server.Url + "/mcp", authorization_servers = new[] { server.Url }, scopes_supported = new[] { "mcp" } }));
        server.Given(Request.Create().WithPath("/.well-known/oauth-authorization-server").UsingGet()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBodyAsJson(new { issuer = server.Url, authorization_endpoint = server.Url + "/authorize", token_endpoint = server.Url + "/token", response_types_supported = new[] { "code" }, grant_types_supported = new[] { "authorization_code" }, code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" } }));
        server.Given(Request.Create().WithPath("/token").UsingPost()).RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
            .WithBody("""{"access_token":"fixture-access-token","token_type":"Bearer","expires_in":3600,"scope":"mcp"}"""));
        server.Given(Request.Create().WithPath("/mcp").UsingPost().WithHeader("Authorization", "Bearer fixture-access-token")).AtPriority(5)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"jsonrpc":"2.0","id":{{request.bodyAsJson.id}},"error":{"code":-32601,"message":"Method not found"}}""").WithTransformer());
        server.Given(Request.Create().WithPath("/mcp").UsingPost().WithHeader("Authorization", "Bearer fixture-access-token").WithBody(new JsonPartialMatcher("""{"method":"initialize"}"""))).AtPriority(1)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"jsonrpc":"2.0","id":{{request.bodyAsJson.id}},"result":{"protocolVersion":"2025-11-25","capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"}}}""").WithTransformer());
        server.Given(Request.Create().WithPath("/mcp").UsingPost().WithHeader("Authorization", "Bearer fixture-access-token").WithBody(new JsonPartialMatcher("""{"method":"tools/list"}"""))).AtPriority(1)
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"jsonrpc":"2.0","id":{{request.bodyAsJson.id}},"result":{"tools":[]}}""").WithTransformer());
        server.Given(Request.Create().WithPath("/mcp").UsingPost().WithHeader("Authorization", "Bearer fixture-access-token").WithBody(new JsonPartialMatcher("""{"method":"notifications/initialized"}"""))).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(202));
        var callbackCount = 0;
        await using var connections = new McpOAuthConnections([new("fixture", new Uri(server.Url + "/mcp"), _ => new ClientOAuthOptions
        {
            ClientId = "fixture-client", RedirectUri = new Uri("http://127.0.0.1:49157/callback"),
            AuthorizationCallbackHandler = (callback, _) =>
            {
                callbackCount++;
                var query = System.Web.HttpUtility.ParseQueryString(callback.AuthorizationUri.Query);
                query["code_challenge_method"].ShouldBe("S256");
                query["code_challenge"].ShouldNotBeNullOrWhiteSpace();
                return Task.FromResult<AuthorizationResult?>(new() { Code = "fixture-code", State = validState ? query["state"] : "wrong-state" });
            },
        })], true);
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid(), Limits = new() { ProcessTimeout = TimeSpan.FromSeconds(10) } };
        if (validState)
        {
            (await connections.ConnectAsync("fixture", context, TestContext.Current.CancellationToken)).Connected.ShouldBeTrue();
            (await connections.StatusAsync("fixture", new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() }, TestContext.Current.CancellationToken)).Connected.ShouldBeFalse();
            await connections.DisconnectAsync("fixture", context, TestContext.Current.CancellationToken);
            (await connections.StatusAsync("fixture", context, TestContext.Current.CancellationToken)).Connected.ShouldBeFalse();
        }
        else
        {
            await Should.ThrowAsync<IOException>(() => connections.ConnectAsync("fixture", context, TestContext.Current.CancellationToken));
            server.LogEntries.ShouldNotContain(entry => entry.RequestMessage != null && entry.RequestMessage.Path == "/token");
        }
        callbackCount.ShouldBe(1);
    }

    [Fact]
    public async Task Should_RequireTrustedConsent_When_AuthenticatingConfiguredServers()
    {
        var connections = Substitute.For<IMcpConnections>();
        connections.Servers.Returns(new[] { "docs" });
        var interaction = Substitute.For<IUserInteraction>();
        interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(new[] { new QuestionAnswer(["Cancel"], null) });
        var tool = new McpAuthTool(connections, interaction);
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        var input = ToolArguments.Parse("""{"operation":"connect","server":"docs"}""");
        (await tool.PlanAsync(input, context, TestContext.Current.CancellationToken)).Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Ask);
        (await tool.ExecuteAsync(input, context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
        await connections.DidNotReceive().ConnectAsync(Arg.Any<string>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>());
        await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"operation":"connect","server":"unknown"}"""), context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_RejectUnsafeOAuthCallbacksBeforeNetwork_When_Connecting()
    {
        await using var connections = new McpOAuthConnections([new("docs", new Uri("https://docs.example/mcp"), _ => new ClientOAuthOptions { RedirectUri = new Uri("http://remote.example/callback") })]);
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
        (await connections.StatusAsync("docs", context, TestContext.Current.CancellationToken)).Connected.ShouldBeFalse();
        await Should.ThrowAsync<ArgumentException>(() => connections.ConnectAsync("docs", context, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(() => connections.ListAsync("docs", context, TestContext.Current.CancellationToken));
        await connections.DisconnectAsync("docs", context, TestContext.Current.CancellationToken);
    }
}