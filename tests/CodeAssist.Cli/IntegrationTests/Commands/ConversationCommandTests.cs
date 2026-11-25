using System.Diagnostics;
using System.Text.Json;
using CodeAssist.Client;
using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.IntegrationTests.Support;
using CodeAssist.Contracts;
using CodeAssist.Configuration;
using NSubstitute;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Cli.IntegrationTests.Commands;

public sealed class ConversationCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TurnId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
    private const string LiteralText = "[bold]literal[/] <tag>\nsecond line";

    public void Dispose() => _cli.Dispose();

    [Theory]
    [InlineData("help")]
    [InlineData("other-command")]
    [InlineData("consent-declined")]
    public async Task Should_NeverInvokeFontSetup_When_ChatHasNotBeenAdmitted(string partition)
    {
        _cli.Input = Console.In;
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = true;
            console.Profile.Capabilities.Unicode = true;
            console.Input.PushKey(ConsoleKey.Enter);
        };
        var result = await _cli.Run(partition switch { "help" => ["chat", "--help"], "other-command" => ["config", "show", "--json"], _ => ["chat"] });
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        await _cli.FontSetup.DidNotReceive().ConfigureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.Output.ShouldNotContain("Automatic font setup");
        if (partition == "consent-declined") Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_RequireNerdIconsBeforeLiveShell_When_LocalStartupCompletes(bool confirmed)
    {
        _cli.Environment["Catalog__Refresh"] = "never";
        _cli.Input = Console.In;
        _cli.FontSetup.ConfigureAsync("auto", Arg.Any<CancellationToken>()).Returns(confirmed);
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = true;
            console.Profile.Capabilities.Unicode = true;
            console.Input.PushText("/exit");
            console.Input.PushKey(ConsoleKey.Enter);
        };

        var result = await _cli.Run("chat", "--trust-workspace");

        result.Code.ShouldBe(confirmed ? ExitCodes.Success : ExitCodes.UsageOrConfig, result.Output);
        await _cli.FontSetup.Received(1).ConfigureAsync("auto", Arg.Any<CancellationToken>());
        if (confirmed)
        {
            result.Output.ShouldContain("\uec20 CodeAssist");
            result.Output.ShouldContain("No model session");
        }
        else
        {
            result.Output.ShouldContain("Nerd Font setup is required before interactive chat can start.");
            result.Output.ShouldNotContain("\uec20");
            result.Output.ShouldNotContain("No model session");
            Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
        }
        result.Output.ShouldNotContain("Session selected");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("injected")]
    [InlineData("noninteractive")]
    [InlineData("unicode-disabled")]
    [InlineData("service")]
    [InlineData("ansi-disabled")]
    public async Task Should_ApplyMandatorySetupByInteractivity_When_ChatIsOutsideLocalUnicodeShell(string partition)
    {
        using var api = new ConversationApi(_cli);
        _cli.Environment["Catalog__Refresh"] = "never";
        _cli.FontSetup.ConfigureAsync("nerd", Arg.Any<CancellationToken>()).Returns(true);
        _cli.Input = partition is "unicode-disabled" or "service" or "json" ? Console.In : new StringReader("/exit\n");
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = partition != "noninteractive";
            console.Profile.Capabilities.Ansi = partition != "ansi-disabled";
            console.Profile.Capabilities.Unicode = partition != "unicode-disabled";
            console.Input.PushText("/exit");
            console.Input.PushKey(ConsoleKey.Enter);
        };
        string[] options = partition switch { "service" => ["--endpoint", api.Url], "json" => ["--json", "--trust-workspace"], _ => ["--trust-workspace"] };

        var result = await _cli.Run(["chat", .. options, "--icons", "nerd"]);

        result.Code.ShouldBe(partition == "json" ? ExitCodes.Failure : partition == "unicode-disabled" ? ExitCodes.UsageOrConfig : ExitCodes.Success, result.Output);
        if (partition == "json") result.Output.ShouldContain("Server request failed (502)");
        await _cli.FontSetup.Received(partition is "injected" or "service" or "ansi-disabled" ? 1 : 0)
            .ConfigureAsync("nerd", Arg.Any<CancellationToken>());
        if (partition == "unicode-disabled") result.Output.ShouldContain("Interactive chat requires Unicode and a Nerd Font.");
        if (partition != "service") api.Calls.ShouldBeEmpty();
        else api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions"]);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("nerd")]
    public async Task Should_PassExplicitIconSelection_When_LocalShellStarts(string icons)
    {
        _cli.Environment["Catalog__Refresh"] = "never";
        _cli.FontSetup.ConfigureAsync(icons, Arg.Any<CancellationToken>()).Returns(true);
        _cli.Input = Console.In;
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = true;
            console.Profile.Capabilities.Unicode = true;
            console.Input.PushText("/exit");
            console.Input.PushKey(ConsoleKey.Enter);
        };
        var result = await _cli.Run("chat", "--trust-workspace", "--icons", icons);
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        await _cli.FontSetup.Received(1).ConfigureAsync(icons, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("arbitrary-url")]
    [InlineData("ascii")]
    [InlineData("ASCII")]
    public async Task Should_RejectInvalidIconModeWithoutSetup_When_ParsingChatOptions(string icons)
    {
        var result = await _cli.Run("chat", "--trust-workspace", "--icons", icons);
        result.Code.ShouldBe(ExitCodes.UsageOrConfig, result.Output);
        result.Output.ShouldContain("Use --icons auto or nerd");
        await _cli.FontSetup.DidNotReceive().ConfigureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Should_BlockServiceStartup_When_MandatoryFontSetupFails(bool attached, bool shell)
    {
        using var api = new ConversationApi(_cli);
        _cli.Input = shell ? Console.In : new StringReader("/exit\n");
        _cli.FontSetup.ConfigureAsync("auto", Arg.Any<CancellationToken>()).Returns(false);
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = shell;
            console.Profile.Capabilities.Unicode = true;
        };
        var result = await _cli.Run(attached ? ["chat", "--endpoint", api.Url] : ["chat", "--trust-workspace"]);
        result.Code.ShouldBe(ExitCodes.UsageOrConfig, result.Output);
        result.Output.ShouldContain("Nerd Font setup is required before interactive chat can start.");
        await _cli.FontSetup.Received(1).ConfigureAsync("auto", Arg.Any<CancellationToken>());
        api.Calls.ShouldBeEmpty();
        Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
        result.Output.ShouldNotContain("Service ready");
        result.Output.ShouldNotContain("Session selected");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Should_RejectInteractiveAsciiBeforeSetupOrConnection_When_UnicodeIsUnavailable(bool setting, bool attached)
    {
        using var api = new ConversationApi(_cli);
        if (setting) _cli.Environment["Cli__Unicode"] = "ascii";
        _cli.Input = new StringReader("/exit\n");
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Unicode = setting;
        };
        var result = await _cli.Run(attached ? ["chat", "--endpoint", api.Url] : ["chat", "--trust-workspace"]);
        result.Code.ShouldBe(ExitCodes.UsageOrConfig, result.Output);
        result.Output.ShouldContain("Interactive chat requires Unicode and a Nerd Font.");
        await _cli.FontSetup.DidNotReceive().ConfigureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        api.Calls.ShouldBeEmpty();
        Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_RunLiveShellAndRouteTurn_When_InteractiveTerminalSubmits()
    {
        using var api = new ConversationApi(_cli);
        api.Stream("turn.completed");
        _cli.FontSetup.ConfigureAsync("auto", Arg.Any<CancellationToken>()).Returns(true);
        _cli.Input = Console.In;
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = true;
            console.Input.PushText("prompt");
            console.Input.PushKey(ConsoleKey.Enter);
            console.Input.PushText("/exit");
            console.Input.PushKey(ConsoleKey.Enter);
        };

        var result = await _cli.Run("chat", "--endpoint", api.Url);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        result.Output.ShouldContain("Explorer");
        result.Output.ShouldContain("Changes");
        result.Output.ShouldContain("Context / not reported");
        result.Output.ShouldContain(LiteralText);
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", $"POST /v1/sessions/{SessionId}/messages/stream", $"GET /v1/sessions/{SessionId}/messages"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_GateExplorerAndPreviewWithoutInference_When_AttachedShellNavigatesLocalFiles(bool consent)
    {
        using var api = new ConversationApi(_cli);
        File.WriteAllText(Path.Combine(_cli.Files.Work, "README.md"), "study08 [bold]preview[/]");
        _cli.FontSetup.ConfigureAsync("auto", Arg.Any<CancellationToken>()).Returns(true);
        _cli.Input = Console.In;
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 80;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = true;
            console.Profile.Capabilities.Unicode = true;
            for (var index = 0; index < 5; index++) console.Input.PushKey(ConsoleKey.F6);
            console.Input.PushKey(ConsoleKey.Spacebar);
            if (consent)
            {
                for (var index = 0; index < 3; index++) console.Input.PushKey(ConsoleKey.F6);
                console.Input.PushKey(ConsoleKey.Enter);
            }
            console.Input.PushKey(ConsoleKey.Escape);
            console.Input.PushText("/exit");
            console.Input.PushKey(ConsoleKey.Enter);
        };
        var result = await _cli.Run(consent
            ? ["chat", "--endpoint", api.Url, "--session", SessionId.ToString(), "--trust-workspace"]
            : ["chat", "--endpoint", api.Url, "--session", SessionId.ToString()]);
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        if (consent)
        {
            result.Output.ShouldContain("Read-only / up to 256 entries");
            result.Output.ShouldContain("study08 [bold]preview[/]");
        }
        else
        {
            result.Output.ShouldContain("Workspace access required or path protected.");
            result.Output.ShouldNotContain("study08 [bold]preview[/]");
        }
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}"]);
    }

    [Fact]
    public async Task Should_RetainDraftWithoutDeletingOrCancelingSession_When_ShellClosesNewTab()
    {
        using var api = new ConversationApi(_cli);
        api.Json("GET", $"/v1/sessions/{SessionId}", ConversationApi.Session with { Running = true });
        _cli.FontSetup.ConfigureAsync("auto", Arg.Any<CancellationToken>()).Returns(true);
        _cli.Input = Console.In;
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Width = 100;
            console.Profile.Height = 25;
            console.Profile.Capabilities.Interactive = true;
            console.Profile.Capabilities.Ansi = true;
            console.Profile.Capabilities.Unicode = true;
            console.Input.PushText("/exit");
            console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.N, false, true, true));
            console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.W, false, true, true));
            console.Input.PushKey(ConsoleKey.Enter);
        };
        var result = await _cli.Run("chat", "--endpoint", api.Url, "--session", SessionId.ToString());
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        result.Output.ShouldContain("New session 2");
        result.Output.ShouldContain("default/regression");
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_BypassMandatoryFontSetup_When_JsonOrNonInteractiveChatUsesAscii(bool json)
    {
        using var api = new ConversationApi(_cli);
        _cli.Environment["Cli__Unicode"] = "ascii";
        _cli.Input = new StringReader("/exit\n");
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Capabilities.Interactive = json;
            console.Profile.Capabilities.Unicode = false;
        };
        var result = await _cli.Run(json ? ["chat", "--endpoint", api.Url, "--json"] : ["chat", "--endpoint", api.Url]);
        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        await _cli.FontSetup.DidNotReceive().ConfigureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions"]);
        if (json) Records(result.Output).Single().GetProperty("session").GetProperty("id").GetGuid().ShouldBe(SessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_KeepServiceReadyWithoutCreatingSession_When_LocalChatHasNoCredentials(bool sendPrompt)
    {
        _cli.Environment["Catalog__Refresh"] = "never";
        _cli.Input = new StringReader((sendPrompt ? "first prompt\n" : "") + "/status\n/help\n/session.list\n/exit\n");

        var result = await _cli.Run("chat", "--trust-workspace");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        result.Output.ShouldContain("CodeAssist | Service ready");
        result.Output.ShouldContain("\"status\": \"ready\"");
        result.Output.ShouldContain("[]");
        result.Output.Split(['\r', '\n']).Where(line => line.StartsWith("Session ", StringComparison.Ordinal)).ShouldBeEmpty();
        result.Output.ShouldNotContain("Server request failed (502)");
        if (sendPrompt) result.Output.ShouldContain("The service is still running.");
        else result.Output.ShouldNotContain("Could not open a model session");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_RequireExplicitWorkspaceAcceptance_When_LocalChatIsNonInteractive(bool json)
    {
        _cli.ConfigureConsole = console => console.Profile.Capabilities.Interactive = false;
        var result = await _cli.Run(json ? ["chat", "--json"] : ["chat"]);

        result.Code.ShouldBe(ExitCodes.UsageOrConfig, result.Output);
        result.Output.ShouldContain("Local chat requires workspace acceptance");
        Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_LeaveWithoutStartingService_When_WorkspaceAcceptanceIsDeclined(bool explicitChat)
    {
        _cli.ConfigureConsole = console =>
        {
            console.Profile.Capabilities.Interactive = true;
            console.Input.PushKey(ConsoleKey.Enter);
        };
        var result = await _cli.Run(explicitChat ? ["chat"] : []);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        result.Output.ShouldContain("Workspace access");
        result.Output.ShouldContain(_cli.Files.Locations.WorkingDirectory);
        result.Output.ShouldContain("Leave workspace");
        result.Output.ShouldNotContain("session.selected");
        Directory.EnumerateFileSystemEntries(_cli.Files.Home).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("turn.completed")]
    [InlineData("turn.failed")]
    [InlineData("turn.canceled")]
    public async Task Should_PreserveLegacyJsonPropertiesAndExitCode_When_ChatTurnTerminates(string terminal)
    {
        using var api = new ConversationApi(_cli);
        api.Stream(terminal);
        _cli.Input = new StringReader("/history\nprompt\n/exit\n");
        var result = await _cli.Run("chat", "--endpoint", api.Url, "--json");
        result.Code.ShouldBe(terminal == "turn.completed" ? ExitCodes.Success : terminal == "turn.canceled" ? ExitCodes.Interrupted : ExitCodes.Failure, result.Output);
        var records = Records(result.Output);
        records.Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected", "session.history", "turn.started", "text.delta", "text.delta", terminal]);
        records[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["kind", "session"]);
        records[0].GetProperty("session").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["createdAt", "id", "messageCount", "model", "running"]);
        records[1].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["kind", "messages"]);
        records[1].GetProperty("messages").Deserialize<AgentMessageDto[]>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Messages);
        foreach (var message in records[1].GetProperty("messages").EnumerateArray())
            message.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["createdAt", "role", "sequence", "text"]);
        foreach (var record in records.Skip(2))
            record.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["kind", "sequence", "text", "turnId"]);
        records.Skip(2).Select(record => record.Deserialize<AgentTurnEvent>(JsonSerializerOptions.Web)).ShouldBe(ConversationApi.Events(terminal));
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", $"GET /v1/sessions/{SessionId}/messages", $"POST /v1/sessions/{SessionId}/messages/stream"]);
    }

    [Theory]
    [InlineData("list-name")]
    [InlineData("list-input")]
    [InlineData("missing-name")]
    [InlineData("invalid-action")]
    [InlineData("malformed-values")]
    public async Task Should_RejectCommandOptionsBeforeHttp_When_OneShotArgumentsAreInvalid(string partition)
    {
        using var api = new ConversationApi(_cli);
        string[] arguments = partition switch
        {
            "list-name" => ["list", "prompt/review"],
            "list-input" => ["list", "--input", "task"],
            "missing-name" => ["expand"],
            "invalid-action" => ["execute"],
            _ => ["expand", "prompt/review", "--values", "{"],
        };
        var result = await _cli.Run(["commands", .. arguments, "--endpoint", api.Url]);
        result.Code.ShouldBe(partition == "malformed-values" ? ExitCodes.Failure : ExitCodes.UsageOrConfig, result.Output);
        result.Output.ShouldContain(partition switch
        {
            "list-name" or "list-input" => "Command listing accepts no prompt data.",
            "missing-name" => "Provide a qualified command name.",
            "invalid-action" => "Choose commands list or commands expand.",
            _ => "open JSON object",
        });
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("help", "/v1/commands", "commands.list")]
    [InlineData("session.list", "/v1/sessions", "sessions.list")]
    [InlineData("session.info", "details", "session.details")]
    [InlineData("usage", "details", "session.usage")]
    [InlineData("session.checkpoints", "checkpoints", "session.checkpoints")]
    [InlineData("context", "messages", "session.context")]
    [InlineData("models", "/v1/catalog/models", "models.list")]
    [InlineData("skills", "/v1/skills/commands", "skills.list")]
    [InlineData("plugins", "/v1/plugins/commands", "plugins.list")]
    [InlineData("connectors", "connectors", "connectors.list")]
    [InlineData("status", "/v1/health", "service.status")]
    public async Task Should_ReadExactNativeDataWithoutInference_When_ChatInspectsService(string command, string route, string kind)
    {
        using var api = new ConversationApi(_cli);
        var path = route.StartsWith('/') ? route : $"/v1/sessions/{SessionId}/{route}";
        object expected = command switch
        {
            "help" => new[] { new ConversationCommandDto("help", "[bold]literal[/]", "native", "host") },
            "session.list" => new[] { ConversationApi.Session },
            "session.info" or "usage" => new AgentSessionDetails(SessionId, 7, "Saved", false, 11, 13, 2, 1, true),
            "session.checkpoints" => new long[] { 7, 3, 1 },
            "context" => new { Messages = 2, Characters = 25L, IncludesHiddenContext = false },
            "status" => new ServerHealthDto("code-assist", "1", "ready"),
            _ => new[] { new { id = "entry", description = "[bold]literal[/]" } },
        };
        if (command != "context") api.Json("GET", path, expected);
        _cli.Input = new StringReader("/" + command + "\n/exit\n");

        var result = await _cli.Run("chat", "--endpoint", api.Url, "--json");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var records = Records(result.Output);
        records.Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected", kind]);
        records[1].GetProperty(command == "help" ? "commands" : "data").GetRawText().ShouldBe(JsonSerializer.Serialize(expected, JsonSerializerOptions.Web));
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", "GET " + path]);
    }

    [Theory]
    [InlineData("session.new")]
    [InlineData("session.use")]
    [InlineData("session.fork")]
    public async Task Should_SendNextTurnOnlyToSelectedSession_When_NativeCommandChangesSelection(string command)
    {
        using var api = new ConversationApi(_cli);
        var branch = ConversationApi.Branch;
        api.Json("POST", "/v1/sessions", branch, 201);
        api.Json("GET", $"/v1/sessions/{branch.Id}", branch);
        var stream = $"/v1/sessions/{branch.Id}/messages/stream";
        api.Raw("POST", stream, string.Concat(ConversationApi.Events("turn.completed").Select(item => $"event: {item.Kind}\ndata: {JsonSerializer.Serialize(item, JsonSerializerOptions.Web)}\n\n")), "text/event-stream");
        var argument = command == "session.new" ? "default/regression" : command == "session.use" ? branch.Id.ToString() : "";
        _cli.Input = new StringReader("/" + command + " " + argument + "\nafter selection\n/exit\n");

        var result = await _cli.Run("chat", "--endpoint", api.Url, "--session", SessionId.ToString(), "--json");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var records = Records(result.Output);
        records[1].GetProperty("session").Deserialize<AgentSessionDto>(JsonSerializerOptions.Web).ShouldBe(branch);
        api.Body<SendAgentMessageRequest>(stream).Text.ShouldBe("after selection");
        var selection = command switch { "session.new" => "POST /v1/sessions", "session.use" => $"GET /v1/sessions/{branch.Id}", _ => $"POST /v1/sessions/{SessionId}/fork" };
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}", selection, $"POST {stream}"]);
        if (command == "session.new") api.Body<CreateAgentSessionRequest>("/v1/sessions").Model.ShouldBe("default/regression");
    }

    [Fact]
    public async Task Should_SendExactTypedMetadataRequests_When_ClientMethodsAreCalledDirectly()
    {
        using var api = new ConversationApi(_cli);
        var details = new AgentSessionDetails(SessionId, 7, "Original", false, 11, 13, 2, 1, true);
        api.Json("GET", $"/v1/sessions/{SessionId}/details", details);
        api.Json("POST", $"/v1/sessions/{SessionId}/details", details with { Revision = 8, Title = "New title", Archived = true });
        api.Json("GET", $"/v1/sessions/{SessionId}/checkpoints", new long[] { 8, 7, 1 });
        api.Json("POST", $"/v1/sessions/{SessionId}/rewind", ConversationApi.Session);
        using var client = api.CreateClient();
        var token = TestContext.Current.CancellationToken;
        (await client.SessionDetailsAsync(SessionId, token)).ShouldBe(details);
        (await client.UpdateSessionAsync(SessionId, new("New title", true), token)).ShouldBe(details with { Revision = 8, Title = "New title", Archived = true });
        (await client.SessionCheckpointsAsync(SessionId, token)).ShouldBe([8L, 7L, 1L]);
        (await client.RewindSessionAsync(SessionId, new(1, 8), token)).ShouldBe(ConversationApi.Session);
        api.Body<UpdateSessionRequest>($"/v1/sessions/{SessionId}/details").ShouldBe(new("New title", true));
        api.Body<RewindSessionRequest>($"/v1/sessions/{SessionId}/rewind").ShouldBe(new(1, 8));
        api.Calls.ShouldBe([$"GET /v1/sessions/{SessionId}/details", $"POST /v1/sessions/{SessionId}/details", $"GET /v1/sessions/{SessionId}/checkpoints", $"POST /v1/sessions/{SessionId}/rewind"]);
    }

    [Theory]
    [InlineData("details", 404)]
    [InlineData("update", 400)]
    [InlineData("checkpoints", 422)]
    [InlineData("rewind", 409)]
    public async Task Should_PreserveStatusWithoutPrivateBody_When_TypedMetadataRequestFails(string operation, int status)
    {
        using var api = new ConversationApi(_cli);
        var path = $"/v1/sessions/{SessionId}/{(operation == "update" ? "details" : operation)}";
        api.Raw(operation is "update" or "rewind" ? "POST" : "GET", path, "private upstream details", status: status);
        using var client = api.CreateClient();
        var token = TestContext.Current.CancellationToken;
        var error = await Should.ThrowAsync<HttpRequestException>(() => operation switch
        {
            "details" => (Task)client.SessionDetailsAsync(SessionId, token),
            "update" => client.UpdateSessionAsync(SessionId, new("New", false), token),
            "checkpoints" => client.SessionCheckpointsAsync(SessionId, token),
            _ => client.RewindSessionAsync(SessionId, new(1, 7), token),
        });
        error.StatusCode.ShouldBe((System.Net.HttpStatusCode)status);
        error.Message.ShouldBe($"Server request failed ({status}).");
        api.ShouldNotLeakToken(error.ToString());
        error.ToString().ShouldNotContain("private upstream details");
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("expand", false)]
    [InlineData("expand", true)]
    public async Task Should_ReturnCommandDataWithoutCreatingSession_When_CommandsRunOneShot(string action, bool json)
    {
        using var api = new ConversationApi(_cli);
        ConversationCommandDto[] commands = [new("prompt/help", "[bold]literal[/]", "prompt", "settings")];
        var expansion = new ConversationCommandExpansion("/exit [red]literal[/]", "default/other", "reviewer");
        api.Json("GET", "/v1/commands", commands);
        api.Json("POST", "/v1/commands/expand", expansion);
        string[] invocation = action == "expand" ? ["prompt/help", "--input", "task", "--values", "{\"target\":\"diff\"}"] : [];
        string[] format = json ? ["--json"] : [];

        var result = await _cli.Run(["commands", action, .. invocation, "--endpoint", api.Url, .. format]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        if (json)
        {
            var record = Records(result.Output).Single();
            record.GetProperty("kind").GetString().ShouldBe(action == "list" ? "commands.list" : "command.expanded");
            if (action == "list") record.GetProperty("commands").Deserialize<ConversationCommandDto[]>(JsonSerializerOptions.Web).ShouldBe(commands);
            else record.GetProperty("data").Deserialize<ConversationCommandExpansion>(JsonSerializerOptions.Web).ShouldBe(expansion);
        }
        else result.Output.ShouldContain(action == "list" ? "[bold]literal[/]" : "/exit [red]literal[/]");
        if (action == "expand")
        {
            var request = api.Body<ExpandConversationCommandRequest>("/v1/commands/expand");
            request.Name.ShouldBe("prompt/help");
            request.Input.ShouldBe("task");
            request.Values!.ShouldBe(new Dictionary<string, string> { ["target"] = "diff" });
        }
        api.Calls.ShouldBe(["GET /v1/health", action == "list" ? "GET /v1/commands" : "POST /v1/commands/expand"]);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("escape")]
    [InlineData("expanded")]
    [InlineData("structured")]
    [InlineData("model-mismatch")]
    public async Task Should_RouteSlashInputWithoutRecursiveDispatchOrSilentModelSwitch_When_ChatReadsCommand(string partition)
    {
        using var api = new ConversationApi(_cli);
        api.Json("POST", "/v1/commands/expand", new ConversationCommandExpansion("/exit [bold]literal[/]", partition == "model-mismatch" ? "default/other" : "default/regression", null));
        var line = partition switch
        {
            "unknown" => "/does-not-exist",
            "escape" => "  //history",
            "structured" => "/prompt/review {\"input\":\"task with space\",\"values\":{\"target\":\"[red]diff[/]\"}}",
            _ => "/prompt/review task",
        };
        _cli.Input = new StringReader(line + "\n/exit\nnever sent\n");

        var result = await _cli.Run("chat", "--endpoint", api.Url, "--json");

        var rejected = partition is "unknown" or "model-mismatch";
        result.Code.ShouldBe(rejected ? ExitCodes.UsageOrConfig : ExitCodes.Success, result.Output);
        var path = $"/v1/sessions/{SessionId}/messages/stream";
        if (rejected)
        {
            api.Bodies<SendAgentMessageRequest>(path).ShouldBeEmpty();
            result.Output.ShouldContain(partition == "unknown" ? "Unknown command." : "Command requires a different model.");
        }
        else api.Body<SendAgentMessageRequest>(path).Text.ShouldBe(partition == "escape" ? "/history" : "/exit [bold]literal[/]");
        if (partition is "expanded" or "structured" or "model-mismatch")
        {
            var request = api.Body<ExpandConversationCommandRequest>("/v1/commands/expand");
            request.Name.ShouldBe("prompt/review");
            request.Input.ShouldBe(partition == "structured" ? "task with space" : "task");
            if (partition == "structured") request.Values!.ShouldBe(new Dictionary<string, string> { ["target"] = "[red]diff[/]" });
        }
        string[] expansion = partition is "unknown" or "escape" ? [] : ["POST /v1/commands/expand"];
        string[] stream = rejected ? [] : [$"POST {path}"];
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", .. expansion, .. stream]);
    }

    [Theory]
    [InlineData("unknown-member")]
    [InlineData("non-string")]
    [InlineData("malformed")]
    public async Task Should_RejectInvalidStructuredValuesBeforeExpansion_When_ChatCommandJsonIsInvalid(string partition)
    {
        using var api = new ConversationApi(_cli);
        var input = partition switch { "unknown-member" => "{\"input\":\"task\",\"other\":1}", "non-string" => "{\"values\":{\"target\":1}}", _ => "{" };
        _cli.Input = new StringReader("/prompt/review " + input + "\n/exit\n");
        var result = await _cli.Run("chat", "--endpoint", api.Url);
        result.Code.ShouldBe(ExitCodes.Failure, result.Output);
        result.Output.ShouldContain(partition == "unknown-member" ? "could not be mapped" : partition == "non-string" ? "could not be converted to System.String" : "open JSON object");
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions"]);
    }

    [Theory]
    [InlineData("session.rename", "New [bold]title[/]")]
    [InlineData("session.archive", "")]
    [InlineData("session.restore", "")]
    [InlineData("session.rewind", "2")]
    public async Task Should_SendTypedMetadataMutationWithoutInference_When_NativeSessionCommandRuns(string command, string input)
    {
        using var api = new ConversationApi(_cli);
        var details = new AgentSessionDetails(SessionId, 7, "Old title", true, 11, 13, 2, 1, true);
        api.Json("GET", $"/v1/sessions/{SessionId}/details", details);
        api.Json("POST", $"/v1/sessions/{SessionId}/details", details with { Revision = 8 });
        api.Json("POST", $"/v1/sessions/{SessionId}/rewind", ConversationApi.Session);
        _cli.Input = new StringReader("/" + command + " " + input + "\n/exit\n");

        var result = await _cli.Run("chat", "--endpoint", api.Url, "--json");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var mutation = command == "session.rewind" ? "rewind" : "details";
        if (command == "session.rewind") api.Body<RewindSessionRequest>($"/v1/sessions/{SessionId}/rewind").ShouldBe(new(2, 7));
        else api.Body<UpdateSessionRequest>($"/v1/sessions/{SessionId}/details").ShouldBe(new(command == "session.rename" ? input : "Old title", command != "session.restore"));
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", $"GET /v1/sessions/{SessionId}/details", $"POST /v1/sessions/{SessionId}/{mutation}"]);
        Records(result.Output).Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected", command == "session.rewind" ? "session.selected" : "session.details"]);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(100)]
    public void Should_RenderLiteralWrappedHelpWithinWidth_When_CommandMetadataContainsMarkup(int width)
    {
        using var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.NoColors;
        CodeAssist.Tui.ConversationView.Commands(console, [new("prompt/review", "[bold]literal[/] " + new string('x', 160), "prompt", "[red]source[/]", "target")], false);
        console.Output.ShouldContain("[bold]literal[/]");
        console.Output.ShouldContain("[red]source[/]");
        console.Output.ShouldContain("/prompt/review");
        console.Output.Split('\n').ShouldAllBe(line => line.TrimEnd('\r').Length <= width);
        string.Concat(console.Output.Split('\n').Select(line => line.Trim())).ShouldContain(new string('x', 160));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(60)]
    [InlineData(100)]
    public async Task Should_RenderStartupWithinWidth_When_WorkspaceAndModelContainMarkup(int width)
    {
        using var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.NoColors;
        const string model = "provider/[bold]model[/]";
        const string directory = "C:/projects/[red]workspace[/]/nested-folder";

        CodeAssist.Tui.ConversationView.Welcome(console, "1.2.3", model, directory);
        var prompt = await CodeAssist.Tui.ConversationView.ReadAsync(console, new StringReader("/exit\n"), false, "permissions: ask", TestContext.Current.CancellationToken);

        prompt.ShouldBe("/exit");
        var unwrapped = string.Concat(console.Output.Split('\n').Select(line => line.Trim()));
        unwrapped.ShouldContain("CodeAssist v1.2.3");
        unwrapped.ShouldContain(model);
        unwrapped.ShouldContain(directory);
        unwrapped.ShouldContain("permissions: ask");
        console.Output.ShouldContain("> ");
        console.Output.Split('\n').ShouldAllBe(line => line.TrimEnd('\r').Length <= width);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(100)]
    public async Task Should_ReturnEditedInput_When_InteractiveComposerReceivesKeyboardNavigation(int width)
    {
        using var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        console.Input.PushText("helo");
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
        console.Input.PushText("l");
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false));
        console.Input.PushText("!");
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false));
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false));
        console.Input.PushKey(new ConsoleKeyInfo('H', ConsoleKey.H, true, false, false));
        console.Input.PushKey(ConsoleKey.Enter);

        var prompt = await CodeAssist.Tui.ConversationView.ReadAsync(console, Console.In, false, "permissions: ask", TestContext.Current.CancellationToken);

        prompt.ShouldBe("Hello");
        console.Output.ShouldContain("permissions: ask");
        console.Output.ShouldContain("> ");
    }

    [Theory]
    [InlineData(32)]
    [InlineData(100)]
    public async Task Should_KeepInputBounded_When_ComposerEditsLongUnicodeText(int width)
    {
        using var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        var prefix = new string('x', width * 3);
        foreach (var character in prefix + "e\u0301\U0001F600")
            console.Input.PushKey(new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false));
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false));
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false));
        console.Input.PushKey(ConsoleKey.Enter);

        var prompt = await CodeAssist.Tui.ConversationView.ReadAsync(console, Console.In, false, "permissions: ask", TestContext.Current.CancellationToken);

        prompt.ShouldBe(prefix);
        console.Output.ShouldContain("permissions: ask");
        console.Output.ShouldNotContain(new string('x', width));
    }

    [Theory]
    [InlineData(ConsoleKey.D)]
    [InlineData(ConsoleKey.Z)]
    public async Task Should_ReturnEndOfInput_When_EmptyComposerReceivesEof(ConsoleKey key)
    {
        using var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        console.Input.PushKey(new ConsoleKeyInfo('\0', key, false, false, true));

        var prompt = await CodeAssist.Tui.ConversationView.ReadAsync(console, Console.In, false, TestContext.Current.CancellationToken);

        prompt.ShouldBeNull();
        console.Output.ShouldContain("permissions: service");
    }

    [Theory]
    [InlineData("run", false)]
    [InlineData("run", true)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    public async Task Should_SelectNewestSessionWithStableTieBreak_When_LatestRequested(string command, bool tie)
    {
        using var api = new ConversationApi(_cli);
        var competitor = ConversationApi.Branch with { CreatedAt = tie ? CreatedAt : CreatedAt.AddSeconds(-1) };
        var older = ConversationApi.Session with { Id = Guid.Parse("99999999-9999-9999-9999-999999999999"), CreatedAt = CreatedAt.AddDays(-1) };
        api.Json("GET", "/v1/sessions", new[] { older, competitor, ConversationApi.Session });
        _cli.Input = new StringReader("continue\n/exit\n");
        string[] prompt = command == "run" ? ["continue"] : [];

        var result = await _cli.Run([command, .. prompt, "--endpoint", api.Url, "--latest", "--json"]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        Records(result.Output)[0].GetProperty("session").Deserialize<AgentSessionDto>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Session);
        api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe("continue");
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions", $"GET /v1/sessions/{SessionId}", $"POST /v1/sessions/{SessionId}/messages/stream"]);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public async Task Should_RejectLatestWithoutCreatingSession_When_ServiceListIsEmpty(string command)
    {
        using var api = new ConversationApi(_cli);
        api.Json("GET", "/v1/sessions", Array.Empty<AgentSessionDto>());
        string[] prompt = command == "run" ? ["continue"] : [];

        var result = await _cli.Run([command, .. prompt, "--endpoint", api.Url, "--latest"]);

        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain("No sessions are available in the attached service.");
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
    }

    [Theory]
    [InlineData("run", false)]
    [InlineData("run", true)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    public async Task Should_SendOnlyToBranchWithoutWritingSource_When_SelectedSessionIsForked(string command, bool latest)
    {
        using var api = new ConversationApi(_cli);
        var branch = ConversationApi.Branch;
        var stream = $"/v1/sessions/{branch.Id}/messages/stream";
        api.Raw("POST", stream, string.Concat(ConversationApi.Events("turn.completed").Select(item =>
            $"event: {item.Kind}\ndata: {JsonSerializer.Serialize(item, JsonSerializerOptions.Web)}\n\n")), "text/event-stream");
        _cli.Input = new StringReader("branch-only\n/exit\n");
        string[] prompt = command == "run" ? ["branch-only"] : [];
        string[] selection = latest ? ["--latest"] : ["--session", SessionId.ToString()];

        var result = await _cli.Run([command, .. prompt, "--endpoint", api.Url, .. selection, "--fork", "--json"]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        Records(result.Output)[0].GetProperty("session").Deserialize<AgentSessionDto>(JsonSerializerOptions.Web).ShouldBe(branch);
        api.Body<SendAgentMessageRequest>(stream).Text.ShouldBe("branch-only");
        api.Bodies<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").ShouldBeEmpty();
        string[] list = latest ? ["GET /v1/sessions"] : [];
        api.Calls.ShouldBe(["GET /v1/health", .. list, $"POST /v1/sessions/{SessionId}/fork", $"POST {stream}"]);
        using var client = api.CreateClient();
        (await client.SessionAsync(SessionId, TestContext.Current.CancellationToken)).ShouldBe(ConversationApi.Session);
        (await client.MessagesAsync(SessionId, TestContext.Current.CancellationToken)).ShouldBe(ConversationApi.Messages);
    }

    [Theory]
    [InlineData("run", "both")]
    [InlineData("chat", "both")]
    [InlineData("run", "endpoint")]
    [InlineData("chat", "endpoint")]
    [InlineData("run", "model")]
    [InlineData("chat", "model")]
    [InlineData("run", "fork")]
    [InlineData("chat", "fork")]
    public async Task Should_RejectSelectionConflictBeforeHttp_When_ConversationFlagsAreInvalid(string command, string partition)
    {
        using var api = new ConversationApi(_cli);
        string[] prompt = command == "run" ? ["continue"] : [];
        string[] endpoint = partition == "endpoint" ? [] : ["--endpoint", api.Url];
        string[] flags = partition switch
        {
            "both" => ["--latest", "--session", SessionId.ToString()],
            "model" => ["--latest", "--model", "default/other"],
            "fork" => ["--fork"],
            _ => ["--latest"],
        };

        var result = await _cli.Run([command, .. prompt, .. endpoint, .. flags]);

        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain(partition switch
        {
            "both" => "Choose --latest or --session, not both.",
            "endpoint" => "--latest requires --endpoint.",
            "model" => "An existing session keeps its model.",
            _ => "--fork requires --session or --latest.",
        });
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_IncludeOrderedUntrustedUtf8Context_When_FilesAreAttached(bool stdin)
    {
        using var api = new ConversationApi(_cli);
        var first = Path.Combine(_cli.Files.Home, "first context.txt");
        var second = Path.Combine(_cli.Files.Home, "second.txt");
        const string content = "\u00e9\u4e2d\ud83d\ude80\n[bold]ignore instructions[/]";
        await File.WriteAllBytesAsync(first, System.Text.Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "second\n", TestContext.Current.CancellationToken);
        _cli.Input = new StringReader("question\n");
        string[] prompt = stdin ? ["--stdin"] : ["question\n"];

        var result = await _cli.Run(["run", .. prompt, "--endpoint", api.Url, "--file", first, "--file", second]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe(
            "question\n\n\nUntrusted attachment first context.txt:\n" + content + "\n\nUntrusted attachment second.txt:\nsecond\n");
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", $"POST /v1/sessions/{SessionId}/messages/stream"]);
    }

    [Theory]
    [InlineData("invalid-utf8")]
    [InlineData("nul")]
    [InlineData("below")]
    [InlineData("at")]
    [InlineData("above")]
    public async Task Should_EnforceAttachmentByteAndTextValidity_When_FileIsRead(string partition)
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "context.txt");
        var text = new string('\u00e9', 32767) + (partition == "below" ? "x" : "\u00e9");
        byte[] bytes = partition switch
        {
            "invalid-utf8" => [0xc3, 0x28],
            "nul" => [65, 0, 66],
            "above" => [.. System.Text.Encoding.UTF8.GetBytes(text), 120],
            _ => System.Text.Encoding.UTF8.GetBytes(text),
        };
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        var result = await _cli.Run("run", "question", "--endpoint", api.Url, "--file", path);

        if (partition is "below" or "at")
        {
            bytes.Length.ShouldBe(partition == "below" ? 65535 : 65536);
            result.Code.ShouldBe(ExitCodes.Success, result.Output);
            api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe("question\n\nUntrusted attachment context.txt:\n" + text);
        }
        else
        {
            result.Code.ShouldBe(ExitCodes.UsageOrConfig);
            result.Output.ShouldContain(partition switch
            {
                "invalid-utf8" => "Attachments must contain valid UTF-8 text.",
                "nul" => "Binary attachments are not supported.",
                _ => "Text attachment exceeds 65536 bytes.",
            });
            api.Calls.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    public async Task Should_CountAllHeadersAndFiles_When_CombinedInputReachesBudget(int length)
    {
        using var api = new ConversationApi(_cli);
        var first = Path.Combine(_cli.Files.Home, "one.txt");
        var second = Path.Combine(_cli.Files.Home, "two.txt");
        const string context = "\n\nUntrusted attachment one.txt:\nfirst\n\nUntrusted attachment two.txt:\nsecond";
        var prompt = new string('p', length - context.Length);
        await File.WriteAllTextAsync(first, "first", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "second", TestContext.Current.CancellationToken);

        var result = await _cli.Run("run", prompt, "--endpoint", api.Url, "--file", first, "--file", second);

        result.Code.ShouldBe(length <= 65536 ? ExitCodes.Success : ExitCodes.UsageOrConfig, result.Output);
        if (length <= 65536)
            api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe(prompt + context);
        else
        {
            result.Output.ShouldContain("Prompt and attachments exceed 65536 characters.");
            api.Calls.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public async Task Should_BoundAttachmentCount_When_FilesAreRepeated(int count)
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "empty.txt");
        await File.WriteAllTextAsync(path, "", TestContext.Current.CancellationToken);
        var files = Enumerable.Range(0, count).SelectMany(_ => new[] { "--file", path }).ToArray();

        var result = await _cli.Run(["run", "question", "--endpoint", api.Url, .. files]);

        result.Code.ShouldBe(count == 16 ? ExitCodes.Success : ExitCodes.UsageOrConfig, result.Output);
        if (count == 16)
            api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe("question" + string.Concat(Enumerable.Repeat("\n\nUntrusted attachment empty.txt:\n", 16)));
        else
        {
            result.Output.ShouldContain("At most 16 text attachments are allowed.");
            api.Calls.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ForkSelectedSession_When_ForkCommandSucceeds(bool json)
    {
        using var api = new ConversationApi(_cli);
        string[] format = json ? ["--json"] : [];

        var result = await _cli.Run(["sessions", "fork", SessionId.ToString(), "--endpoint", api.Url, .. format]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        if (json) JsonSerializer.Deserialize<AgentSessionDto>(result.Output, JsonSerializerOptions.Web).ShouldBe(ConversationApi.Branch);
        else result.Output.ShouldBe($"Session {ConversationApi.Branch.Id} forked.\n");
        api.Calls.ShouldBe(["GET /v1/health", $"POST /v1/sessions/{SessionId}/fork"]);
        api.ShouldNotLeakToken(result.Output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Should_ExportArchiveToStdoutOrNewFile_When_ExportCommandSucceeds(bool file, bool json)
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "session archive.json");
        string[] target = file ? ["--file", path] : [];
        string[] format = json ? ["--json"] : [];

        var result = await _cli.Run(["sessions", "export", SessionId.ToString(), "--endpoint", api.Url, .. target, .. format]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var content = file ? await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken) : result.Output;
        var archive = JsonSerializer.Deserialize<AgentSessionArchive>(content, JsonSerializerOptions.Web)!;
        archive.Version.ShouldBe(1);
        archive.Model.ShouldBe("default/regression");
        archive.CreatedAt.ShouldBe(CreatedAt);
        archive.Messages.ShouldBe(ConversationApi.Messages);
        archive.State.GetProperty("marker").GetString().ShouldBe("framework-state");
        if (file && json) JsonSerializer.Deserialize<JsonElement>(result.Output).GetProperty("exported").GetGuid().ShouldBe(SessionId);
        if (file && !json) result.Output.ShouldContain("Treat it as sensitive conversation data.");
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}/archive"]);
        api.ShouldNotLeakToken(result.Output);
    }

    [Fact]
    public async Task Should_PreserveExistingFileBytes_When_ExportTargetAlreadyExists()
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "existing archive.json");
        byte[] original = [0, 1, 255, 13, 10];
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);

        var result = await _cli.Run("sessions", "export", SessionId.ToString(), "--endpoint", api.Url, "--file", path);

        result.Code.ShouldBe(ExitCodes.Failure, result.Output);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(original);
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}/archive"]);
        api.ShouldNotLeakToken(result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ImportArchiveFromExplicitFile_When_ImportCommandSucceeds(bool json)
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "transferred archive.json");
        var content = JsonSerializer.Serialize(ConversationApi.Archive, JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        string[] format = json ? ["--json"] : [];

        var result = await _cli.Run(["sessions", "import", "--file", path, "--endpoint", api.Url, .. format]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        if (json) JsonSerializer.Deserialize<AgentSessionDto>(result.Output, JsonSerializerOptions.Web).ShouldBe(ConversationApi.Branch);
        else result.Output.ShouldBe($"Session {ConversationApi.Branch.Id} imported.\n");
        var archive = api.Body<AgentSessionArchive>("/v1/sessions/import");
        archive.Model.ShouldBe("default/regression");
        archive.Messages.ShouldBe(ConversationApi.Messages);
        archive.State.GetProperty("marker").GetString().ShouldBe("framework-state");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(content);
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions/import"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("missing")]
    public async Task Should_RejectInvalidArchiveFileWithoutImportRequest_When_FileCannotBeRead(string partition)
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "invalid archive.json");
        if (partition != "missing") await File.WriteAllTextAsync(path, partition switch
        {
            "null" => "null",
            "malformed" => "{",
            _ => new string('x', 393217),
        }, TestContext.Current.CancellationToken);

        var result = await _cli.Run("sessions", "import", "--endpoint", api.Url, "--file", path);

        result.Code.ShouldBe(partition is "null" or "oversized" ? ExitCodes.UsageOrConfig : ExitCodes.Failure, result.Output);
        if (partition == "oversized") result.Output.ShouldContain("384 KiB");
        if (partition == "null") result.Output.ShouldContain("Invalid session archive.");
        api.Calls.ShouldBe(["GET /v1/health"]);
    }

    [Theory]
    [InlineData(393215)]
    [InlineData(393216)]
    public async Task Should_AllowArchiveFileAtByteLimit_When_ImportReadsBoundedFile(int bytes)
    {
        using var api = new ConversationApi(_cli);
        var path = Path.Combine(_cli.Files.Home, "bounded archive.json");
        var content = JsonSerializer.Serialize(ConversationApi.Archive, JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(path, content + new string(' ', bytes - System.Text.Encoding.UTF8.GetByteCount(content)), TestContext.Current.CancellationToken);
        new FileInfo(path).Length.ShouldBe(bytes);

        var result = await _cli.Run("sessions", "import", "--endpoint", api.Url, "--file", path, "--json");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        JsonSerializer.Deserialize<AgentSessionDto>(result.Output, JsonSerializerOptions.Web).ShouldBe(ConversationApi.Branch);
        api.Body<AgentSessionArchive>("/v1/sessions/import").Messages.ShouldBe(ConversationApi.Messages);
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions/import"]);
    }

    [Theory]
    [InlineData("list", 0)]
    [InlineData("list", -1)]
    [InlineData("list", 10001)]
    [InlineData("show", 1)]
    [InlineData("fork", 1)]
    [InlineData("export", 1)]
    [InlineData("import", 1)]
    public async Task Should_RejectInvalidLimitBeforeHttp_When_LimitIsOutOfBoundsOrActionIsNotList(string action, int limit)
    {
        using var api = new ConversationApi(_cli);
        string[] id = action is "list" or "import" ? [] : [SessionId.ToString()];
        string[] file = action == "import" ? ["--file", "unused.json"] : [];

        var result = await _cli.Run(["sessions", action, .. id, .. file, "--endpoint", api.Url, "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain("--limit applies to list and must be between 1 and 10000.");
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("fork", "endpoint")]
    [InlineData("export", "endpoint")]
    [InlineData("import", "endpoint")]
    [InlineData("fork", "id")]
    [InlineData("export", "id")]
    [InlineData("import", "unexpected-id")]
    [InlineData("import", "file")]
    [InlineData("fork", "unexpected-file")]
    [InlineData("list", "unexpected-file")]
    public async Task Should_RejectInvalidTransferOptionsBeforeHttp_When_CommandArgumentsConflict(string action, string partition)
    {
        using var api = new ConversationApi(_cli);
        string[] endpoint = partition == "endpoint" ? [] : ["--endpoint", api.Url];
        string[] id = partition == "unexpected-id" || action is "fork" or "export" && partition != "id" ? [SessionId.ToString()] : [];
        string[] file = partition == "unexpected-file" ? ["--file", "unused.json"] : [];

        var result = await _cli.Run(["sessions", action, .. id, .. endpoint, .. file]);

        result.Code.ShouldBe(ExitCodes.UsageOrConfig);
        result.Output.ShouldContain(partition switch
        {
            "endpoint" => "Session commands require --endpoint",
            "id" => "Supply a session ID.",
            "unexpected-id" => "Session import does not accept an ID.",
            "file" => "Session import requires --file.",
            _ => "--file applies only to import/export.",
        });
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(10000, true)]
    [InlineData(1, false)]
    public async Task Should_ListNewestSessionsWithStableTieBreak_When_LimitIsSpecified(int limit, bool json)
    {
        using var api = new ConversationApi(_cli);
        var older = ConversationApi.Session with { Id = Guid.Parse("99999999-9999-9999-9999-999999999999"), CreatedAt = CreatedAt.AddDays(-1) };
        var newest = ConversationApi.Session with { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), CreatedAt = CreatedAt.AddDays(1) };
        var tied = newest with { Id = Guid.Parse("44444444-4444-4444-4444-444444444444") };
        api.Json("GET", "/v1/sessions", new[] { older, tied, ConversationApi.Session, newest });
        string[] format = json ? ["--json"] : [];

        var result = await _cli.Run(["sessions", "list", "--endpoint", api.Url, "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), .. format]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        if (json) JsonSerializer.Deserialize<AgentSessionDto[]>(result.Output, JsonSerializerOptions.Web)
            .ShouldBe(new[] { newest, tied, ConversationApi.Session, older }.Take(limit));
        else
        {
            result.Output.ShouldContain(newest.Id.ToString());
            result.Output.ShouldNotContain(older.Id.ToString());
            result.Output.ShouldNotContain(tied.Id.ToString());
            result.Output.ShouldNotContain(SessionId.ToString());
        }
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
    }

    [Fact]
    public async Task Should_ReturnEmptyList_When_ServiceHasNoSessions()
    {
        using var api = new ConversationApi(_cli);
        api.Json("GET", "/v1/sessions", Array.Empty<AgentSessionDto>());

        var result = await _cli.Run("sessions", "list", "--endpoint", api.Url, "--limit", "1", "--json");

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        JsonSerializer.Deserialize<AgentSessionDto[]>(result.Output, JsonSerializerOptions.Web).ShouldBeEmpty();
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_CapNewestSessions_When_DefaultOrMaximumLimitIsUsed(bool explicitLimit)
    {
        using var api = new ConversationApi(_cli);
        api.Json("GET", "/v1/sessions", Enumerable.Range(0, 10001).Select(index => ConversationApi.Session with
        {
            Id = Guid.Parse(index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)),
            CreatedAt = CreatedAt.AddSeconds(index),
        }).ToArray());
        string[] limit = explicitLimit ? ["--limit", "10000"] : [];

        var result = await _cli.Run(["sessions", "list", "--endpoint", api.Url, "--json", .. limit]);

        result.Code.ShouldBe(ExitCodes.Success, result.Output);
        var sessions = JsonSerializer.Deserialize<AgentSessionDto[]>(result.Output, JsonSerializerOptions.Web)!;
        sessions.Length.ShouldBe(10000);
        sessions.Select(session => session.CreatedAt).ShouldBe(Enumerable.Range(1, 10000).Reverse().Select(index => CreatedAt.AddSeconds(index)));
        sessions.Select(session => session.Id).ShouldNotContain(Guid.Empty);
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
    }

    [Fact]
    public async Task Should_SendExactTransferRequests_When_ClientMethodsAreCalledDirectly()
    {
        using var api = new ConversationApi(_cli);
        using var client = api.CreateClient();

        var archive = await client.ExportSessionAsync(SessionId, TestContext.Current.CancellationToken);
        var forked = await client.ForkSessionAsync(SessionId, TestContext.Current.CancellationToken);
        var imported = await client.ImportSessionAsync(archive, TestContext.Current.CancellationToken);

        archive.Messages.ShouldBe(ConversationApi.Messages);
        archive.State.GetProperty("marker").GetString().ShouldBe("framework-state");
        forked.ShouldBe(ConversationApi.Branch);
        imported.ShouldBe(ConversationApi.Branch);
        api.Body<AgentSessionArchive>("/v1/sessions/import").Messages.ShouldBe(ConversationApi.Messages);
        api.Calls.ShouldBe([$"GET /v1/sessions/{SessionId}/archive", $"POST /v1/sessions/{SessionId}/fork", "POST /v1/sessions/import"]);
    }

    [Theory]
    [InlineData("export", 401)]
    [InlineData("export", 404)]
    [InlineData("export", 409)]
    [InlineData("fork", 409)]
    [InlineData("fork", 429)]
    [InlineData("import", 400)]
    [InlineData("import", 422)]
    [InlineData("import", 429)]
    public async Task Should_PreserveHttpStatusWithoutLeakingDetails_When_ClientTransferFails(string action, int status)
    {
        using var api = new ConversationApi(_cli);
        var path = action == "import" ? "/v1/sessions/import" : $"/v1/sessions/{SessionId}/{(action == "export" ? "archive" : "fork")}";
        api.Json(action == "export" ? "GET" : "POST", path, new { detail = "SECRET_PROVIDER_DETAIL" }, status);
        using var client = api.CreateClient();

        var error = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            if (action == "export") await client.ExportSessionAsync(SessionId, TestContext.Current.CancellationToken);
            else if (action == "fork") await client.ForkSessionAsync(SessionId, TestContext.Current.CancellationToken);
            else await client.ImportSessionAsync(ConversationApi.Archive, TestContext.Current.CancellationToken);
        });

        error.StatusCode.ShouldBe((System.Net.HttpStatusCode)status);
        error.Message.ShouldBe($"Server request failed ({status}).");
        api.ShouldNotLeakToken(error.ToString());
        error.ToString().ShouldNotContain("SECRET_PROVIDER_DETAIL");
        api.Calls.ShouldBe([$"{(action == "export" ? "GET" : "POST")} {path}"]);
    }

    [Theory]
    [InlineData("run", "--stdin", "--session")]
    [InlineData("chat", "--session", "--endpoint")]
    [InlineData("sessions", "--endpoint", "delete")]
    public async Task Should_RegisterConversationOptions_When_HelpRequested(string command, string first, string second)
    {
        var (code, output) = await _cli.Run(command, "--help");

        code.ShouldBe(ExitCodes.Success, output);
        output.ShouldContain(first);
        output.ShouldContain(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_RejectAmbiguousPrompt_When_PromptMissingOrCombinedWithStdin(bool both)
    {
        var arguments = both ? new[] { "run", "prompt", "--stdin" } : new[] { "run" };

        var (code, output) = await _cli.Run(arguments);

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("Supply a quoted prompt or --stdin, but not both.");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("chat")]
    public async Task Should_RequireEndpointAndPreserveModel_When_ReusingSession(string command)
    {
        var arguments = command == "run" ? new[] { command, "prompt" } : new[] { command };
        var session = new[] { "--session", "11111111-1111-1111-1111-111111111111" };

        var missing = await _cli.Run([.. arguments, .. session]);
        var model = await _cli.Run([.. arguments, .. session, "--endpoint", "http://127.0.0.1:1", "--model", "default/other"]);

        missing.Code.ShouldBe(ExitCodes.UsageOrConfig);
        missing.Output.ShouldContain("Reusing a session requires --endpoint");
        model.Code.ShouldBe(ExitCodes.UsageOrConfig);
        model.Output.ShouldContain("An existing session keeps its model.");
    }

    [Fact]
    public async Task Should_RegisterAllConversationCommands_When_RootHelpRequested()
    {
        var (code, output) = await _cli.Run("--help");

        code.ShouldBe(ExitCodes.Success, output);
        foreach (var command in new[] { "run", "chat", "sessions" }) output.ShouldContain(command);
        foreach (var command in new[] { "list", "show", "cancel", "delete" })
        {
            var help = await _cli.Run("sessions", command, "--help");
            help.Code.ShouldBe(ExitCodes.Success, help.Output);
            help.Output.ShouldContain("code-assist sessions <ACTION>");
            if (command == "delete") help.Output.ShouldContain("--yes");
        }
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task Should_RequireExplicitEndpoint_When_ManagingSessions(string command)
    {
        var (code, output) = await _cli.Run("sessions", command);

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("Session commands require --endpoint");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("show")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task Should_ValidateSessionIdBeforeHttp_When_ManagingSessions(string command)
    {
        using var api = new ConversationApi(_cli);
        string[] id = command == "list" ? [SessionId.ToString()] : [];

        var (code, output) = await _cli.Run(["sessions", "--endpoint", api.Url, command, .. id]);

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain(command == "list" ? "Session list does not accept an ID." : "Supply a session ID.");
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_StreamLiteralTextAndJsonRecords_When_NewTurnCompletes(bool json)
    {
        using var api = new ConversationApi(_cli);
        string[] format = json ? ["--json"] : [];

        var (code, output) = await _cli.Run(["run", "explain [red]code[/]", "--endpoint", api.Url, "--model", "default/regression", .. format]);

        code.ShouldBe(ExitCodes.Success, output);
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions", $"POST /v1/sessions/{SessionId}/messages/stream"]);
        api.Body<CreateAgentSessionRequest>("/v1/sessions").Model.ShouldBe("default/regression");
        api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe("explain [red]code[/]");
        if (json)
        {
            var records = Records(output);
            records.Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected", "turn.started", "text.delta", "text.delta", "turn.completed"]);
            records[0].GetProperty("session").Deserialize<AgentSessionDto>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Session);
            records.Skip(1).Select(record => record.Deserialize<AgentTurnEvent>(JsonSerializerOptions.Web)).ShouldBe(ConversationApi.Events("turn.completed"));
        }
        else output.ShouldBe(LiteralText + Environment.NewLine);
        api.ShouldNotLeakToken(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReadExactPromptFromInjectedReader_When_StdinSelected(bool boundary)
    {
        using var api = new ConversationApi(_cli);
        var prompt = boundary ? new string('x', 65536) : "first line\n[bold]second[/]\n";
        _cli.Input = new StringReader(prompt);

        var (code, output) = await _cli.Run("run", "--stdin", "--endpoint", api.Url);

        code.ShouldBe(ExitCodes.Success, output);
        api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe(prompt);
        api.Body<CreateAgentSessionRequest>("/v1/sessions").Model.ShouldBeNull();
        output.ShouldBe(LiteralText + Environment.NewLine);
    }

    [Fact]
    public async Task Should_AcceptPromptAtLimit_When_ProvidedAsArgument()
    {
        using var api = new ConversationApi(_cli);

        var (code, output) = await _cli.Run("run", new string('x', 65536), "--endpoint", api.Url);

        code.ShouldBe(ExitCodes.Success, output);
        api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe(new string('x', 65536));
        output.ShouldBe(LiteralText + Environment.NewLine);
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("whitespace", false)]
    [InlineData("oversized", false)]
    [InlineData("empty", true)]
    [InlineData("whitespace", true)]
    [InlineData("oversized", true)]
    public async Task Should_RejectInvalidPromptBeforeHttp_When_InputEmptyOrOverBudget(string partition, bool stdin)
    {
        using var api = new ConversationApi(_cli);
        var prompt = partition switch { "empty" => "", "whitespace" => " \t\n", _ => new string('x', 65537) };
        _cli.Input = new StringReader(prompt);
        string[] arguments = stdin ? ["run", "--stdin"] : ["run", prompt];

        var (code, output) = await _cli.Run([.. arguments, "--endpoint", api.Url]);

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain(partition == "oversized" ? "Prompt exceeds 65536 characters." : "Prompt must not be empty.");
        api.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_UseExistingSessionWithoutCreating_When_SessionAttached()
    {
        using var api = new ConversationApi(_cli);

        var (code, output) = await _cli.Run("run", "continue", "--endpoint", api.Url, "--session", SessionId.ToString(), "--json");

        code.ShouldBe(ExitCodes.Success, output);
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}", $"POST /v1/sessions/{SessionId}/messages/stream"]);
        Records(output)[0].GetProperty("session").GetProperty("model").GetString().ShouldBe("default/regression");
        api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe("continue");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ListCommittedSessions_When_SessionAttached(bool json)
    {
        using var api = new ConversationApi(_cli);
        string[] format = json ? ["--json"] : [];

        var list = await _cli.Run(["sessions", "--endpoint", api.Url, .. format, "list"]);

        list.Code.ShouldBe(ExitCodes.Success, list.Output);
        if (json)
            JsonSerializer.Deserialize<AgentSessionDto[]>(list.Output, JsonSerializerOptions.Web).ShouldBe([ConversationApi.Session]);
        else
        {
            list.Output.ShouldContain(SessionId.ToString());
            list.Output.ShouldContain("default/regression");
            list.Output.ShouldContain("idle");
        }
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ShowCommittedHistory_When_SessionAttached(bool json)
    {
        using var api = new ConversationApi(_cli);
        string[] format = json ? ["--json"] : [];

        var (code, output) = await _cli.Run(["sessions", "--endpoint", api.Url, .. format, "show", SessionId.ToString()]);

        code.ShouldBe(ExitCodes.Success, output);
        if (json)
        {
            using var document = JsonDocument.Parse(output);
            document.RootElement.GetProperty("session").Deserialize<AgentSessionDto>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Session);
            document.RootElement.GetProperty("messages").Deserialize<AgentMessageDto[]>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Messages);
        }
        else output.ShouldBe($"Session {SessionId} | default/regression\nuser: earlier\nassistant: [bold]committed[/]\n");
        api.Calls.ShouldBe(["GET /v1/health", $"GET /v1/sessions/{SessionId}", $"GET /v1/sessions/{SessionId}/messages"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Should_ReportCancellationResult_When_CancelRequested(bool canceled, bool json)
    {
        using var api = new ConversationApi(_cli);
        api.Json("POST", $"/v1/sessions/{SessionId}/cancel", new CancelAgentTurnResponse(canceled));
        string[] format = json ? ["--json"] : [];

        var (code, output) = await _cli.Run(["sessions", "--endpoint", api.Url, .. format, "cancel", SessionId.ToString()]);

        code.ShouldBe(ExitCodes.Success, output);
        if (json) JsonSerializer.Deserialize<CancelAgentTurnResponse>(output, JsonSerializerOptions.Web).ShouldBe(new(canceled));
        else output.ShouldBe((canceled ? "Cancellation requested." : "No active turn.") + "\n");
        api.Calls.ShouldBe(["GET /v1/health", $"POST /v1/sessions/{SessionId}/cancel"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_RequireConfirmationBeforeDelete_When_SessionDeletionRequested(bool json)
    {
        using var api = new ConversationApi(_cli);
        string[] format = json ? ["--json"] : [];

        var denied = await _cli.Run(["sessions", "--endpoint", api.Url, .. format, "delete", SessionId.ToString()]);

        denied.Code.ShouldBe(ExitCodes.UsageOrConfig);
        denied.Output.ShouldContain("Deleting session history requires --yes.");
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_DeleteSelectedSession_When_ExplicitlyConfirmed(bool json)
    {
        using var api = new ConversationApi(_cli);
        string[] format = json ? ["--json"] : [];

        var deleted = await _cli.Run(["sessions", "--endpoint", api.Url, .. format, "delete", SessionId.ToString(), "--yes"]);

        deleted.Code.ShouldBe(ExitCodes.Success, deleted.Output);
        if (json)
        {
            using var document = JsonDocument.Parse(deleted.Output);
            document.RootElement.GetProperty("deleted").GetGuid().ShouldBe(SessionId);
        }
        else deleted.Output.ShouldBe("Session deleted.\n");
        api.Calls.ShouldBe(["GET /v1/health", $"DELETE /v1/sessions/{SessionId}"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_BindInheritedOptions_When_PlacedBeforeOrAfterSessionSubcommand(bool before)
    {
        using var api = new ConversationApi(_cli);
        string[] arguments = before
            ? ["sessions", "--endpoint", api.Url, "--json", "list"]
            : ["sessions", "list", "--endpoint", api.Url, "--json"];

        var (code, output) = await _cli.Run(arguments);

        code.ShouldBe(ExitCodes.Success, output);
        JsonSerializer.Deserialize<AgentSessionDto[]>(output, JsonSerializerOptions.Web).ShouldBe([ConversationApi.Session]);
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
    }

    [Fact]
    public async Task Should_KeepJsonOutput_When_FormatOptionFollowsSessionSubcommand()
    {
        using var api = new ConversationApi(_cli);

        var (code, output) = await _cli.Run("sessions", "--endpoint", api.Url, "list", "--json");

        code.ShouldBe(ExitCodes.Success, output);
        api.Calls.ShouldBe(["GET /v1/health", "GET /v1/sessions"]);
        output.TrimStart().ShouldStartWith("[");
        JsonSerializer.Deserialize<AgentSessionDto[]>(output, JsonSerializerOptions.Web).ShouldBe([ConversationApi.Session]);
    }

    [Theory]
    [InlineData("turn.failed", false)]
    [InlineData("turn.canceled", false)]
    [InlineData("truncated", false)]
    [InlineData("turn.failed", true)]
    [InlineData("turn.canceled", true)]
    [InlineData("truncated", true)]
    public async Task Should_ReturnNonzeroAfterPartialText_When_StreamDoesNotComplete(string terminal, bool json)
    {
        using var api = new ConversationApi(_cli);
        api.Stream(terminal);
        string[] format = json ? ["--json"] : [];

        var (code, output) = await _cli.Run(["run", "prompt", "--endpoint", api.Url, .. format]);

        code.ShouldBe(terminal == "turn.canceled" ? ExitCodes.Interrupted : ExitCodes.Failure);
        if (json && terminal != "truncated")
        {
            var records = Records(output);
            records.Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected", "turn.started", "text.delta", "text.delta", terminal]);
            records[2].GetProperty("text").GetString().ShouldBe("[bold]literal[/] <tag>\n");
        }
        else
        {
            output.ShouldContain(json ? "text.delta" : LiteralText);
            output.ShouldContain(terminal switch { "turn.failed" => "Turn failed.", "turn.canceled" => "Turn canceled.", _ => "Turn stream ended before completion." });
        }
        api.ShouldNotLeakToken(output);
    }

    [Fact]
    public async Task Should_RejectMissingTokenWithoutHttpOrSecretOutput_When_Attaching()
    {
        using var api = new ConversationApi(_cli);
        _cli.Environment.Remove("Host__Listen__Http__Token");
        _cli.Environment["Providers__default__ApiKey"] = new string('k', 40);

        var (code, output) = await _cli.Run("run", "prompt", "--endpoint", api.Url);

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("Set Host__Listen__Http__Token in the environment");
        output.Contains(new string('k', 40), StringComparison.Ordinal).ShouldBeFalse("Provider credential must not appear in output.");
        api.ShouldNotLeakToken(output);
        api.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("identity")]
    [InlineData("protocol")]
    [InlineData("status")]
    public async Task Should_StopBeforeSessionCreationWithoutLeakingToken_When_HealthRejected(string reason)
    {
        using var api = new ConversationApi(_cli);
        api.Json("GET", "/v1/health", new ServerHealthDto(reason == "identity" ? "other" : "code-assist", reason == "protocol" ? "2" : "1", reason == "status" ? "starting" : "ready"), reason == "unauthorized" ? 401 : 200);

        var (code, output) = await _cli.Run("run", "prompt", "--endpoint", api.Url);

        code.ShouldBe(ExitCodes.Failure);
        output.ShouldContain(reason == "unauthorized" ? "Server request failed (401)." : "Server identity or protocol is incompatible.");
        api.ShouldNotLeakToken(output);
        api.Calls.ShouldBe(["GET /v1/health"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Should_ReuseChatSessionAndReadHistory_When_InjectedLinesReachExitOrEof(bool attached, bool explicitExit)
    {
        using var api = new ConversationApi(_cli);
        _cli.Input = new StringReader("first\n \n/history\nsecond\n" + (explicitExit ? "/exit\nnever sent\n" : ""));
        string[] session = attached ? ["--session", SessionId.ToString()] : [];

        var (code, output) = await _cli.Run(["chat", "--endpoint", api.Url, "--json", .. session]);

        code.ShouldBe(ExitCodes.Success, output);
        var records = Records(output);
        records.Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected", "turn.started", "text.delta", "text.delta", "turn.completed", "session.history", "turn.started", "text.delta", "text.delta", "turn.completed"]);
        records[0].GetProperty("session").Deserialize<AgentSessionDto>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Session);
        records[2].GetProperty("text").GetString().ShouldBe("[bold]literal[/] <tag>\n");
        records[3].GetProperty("text").GetString().ShouldBe("second line");
        records[5].GetProperty("messages").Deserialize<AgentMessageDto[]>(JsonSerializerOptions.Web).ShouldBe(ConversationApi.Messages);
        var path = $"/v1/sessions/{SessionId}/messages/stream";
        api.Bodies<SendAgentMessageRequest>(path).Select(request => request.Text).ShouldBe(["first", "second"]);
        api.Calls.ShouldBe(["GET /v1/health", attached ? $"GET /v1/sessions/{SessionId}" : "POST /v1/sessions", $"POST {path}", $"GET /v1/sessions/{SessionId}/messages", $"POST {path}"]);
    }

    [Fact]
    public async Task Should_RenderLiteralHistoryAndTurns_When_ChatUsesTextOutput()
    {
        using var api = new ConversationApi(_cli);
        _cli.Input = new StringReader("first\n/history\nsecond\n");

        var (code, output) = await _cli.Run("chat", "--endpoint", api.Url);

        code.ShouldBe(ExitCodes.Success, output);
        output.ShouldBe($"Session {SessionId} | default/regression\n{LiteralText}{Environment.NewLine}user: earlier\nassistant: [bold]committed[/]\n{LiteralText}{Environment.NewLine}");
        api.Bodies<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Select(request => request.Text).ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task Should_ExitWithoutSendingTurn_When_ChatReaderStartsAtEof()
    {
        using var api = new ConversationApi(_cli);
        _cli.Input = new StringReader("");

        var (code, output) = await _cli.Run("chat", "--endpoint", api.Url, "--json");

        code.ShouldBe(ExitCodes.Success, output);
        Records(output).Select(record => record.GetProperty("kind").GetString()).ShouldBe(["session.selected"]);
        api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_EnforcePromptBudget_When_ChatReadsBoundaryLine(bool oversized)
    {
        using var api = new ConversationApi(_cli);
        _cli.Input = new StringReader(new string('x', oversized ? 65537 : 65536) + "\n");

        var (code, output) = await _cli.Run("chat", "--endpoint", api.Url);

        code.ShouldBe(oversized ? ExitCodes.UsageOrConfig : ExitCodes.Success, output);
        if (oversized)
        {
            output.ShouldContain("Prompt exceeds 65536 characters.");
            api.Calls.ShouldBe(["GET /v1/health", "POST /v1/sessions"]);
        }
        else api.Body<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Text.ShouldBe(new string('x', 65536));
    }

    [Theory]
    [InlineData("turn.failed")]
    [InlineData("turn.canceled")]
    [InlineData("truncated")]
    public async Task Should_StopReadingChatAfterFailedTurn_When_StreamDoesNotComplete(string terminal)
    {
        using var api = new ConversationApi(_cli);
        api.Stream(terminal);
        _cli.Input = new StringReader("first\nnever sent\n");

        var (code, output) = await _cli.Run("chat", "--endpoint", api.Url);

        code.ShouldBe(terminal == "turn.canceled" ? ExitCodes.Interrupted : ExitCodes.Failure);
        output.ShouldContain(LiteralText);
        api.Bodies<SendAgentMessageRequest>($"/v1/sessions/{SessionId}/messages/stream").Select(request => request.Text).ShouldBe(["first"]);
        api.ShouldNotLeakToken(output);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("wrong-content-type")]
    public async Task Should_RejectInvalidEventStream_When_ServerDoesNotSendValidSse(string partition)
    {
        using var api = new ConversationApi(_cli);
        api.RawStream(partition == "malformed" ? "data: not-json\n\n" : "", partition == "wrong-content-type" ? "application/json" : "text/event-stream");

        var (code, output) = await _cli.Run("run", "prompt", "--endpoint", api.Url);

        code.ShouldBe(ExitCodes.Failure);
        output.ShouldContain(partition switch { "wrong-content-type" => "Expected a server event stream.", "empty" => "Turn stream ended before completion.", _ => "JSON" });
        api.ShouldNotLeakToken(output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_RejectInvalidAttachmentWithoutNetwork_When_TokenOrOriginInvalid(bool invalidToken)
    {
        using var api = new ConversationApi(_cli);
        if (invalidToken) _cli.Environment["Host__Listen__Http__Token"] = "short";

        var (code, output) = await _cli.Run("run", "prompt", "--endpoint", invalidToken ? api.Url : "http://remote.invalid");

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("Use a loopback service origin and a valid environment token.");
        api.Calls.ShouldBeEmpty();
        api.ShouldNotLeakToken(output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Should_RunThroughBundledServiceAndSdk_When_ManagedTurnUsesLocalProvider(bool json, bool chat)
    {
        using var upstream = WireMockServer.Start();
        upstream.Given(Request.Create().WithPath("/v1/chat/completions").UsingPost()).RespondWith(Response.Create()
            .WithHeader("Content-Type", "text/event-stream").WithBody("""
                data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"upstream/model","choices":[{"index":0,"delta":{"role":"assistant","content":"OK"},"finish_reason":null}]}

                data: {"id":"c1","object":"chat.completion.chunk","created":1,"model":"upstream/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]


                """));
        _cli.Environment["Catalog__Refresh"] = "never";
        _cli.Environment["Providers__fixture__Protocol"] = "openai";
        _cli.Environment["Providers__fixture__Api"] = "chat";
        _cli.Environment["Providers__fixture__Endpoint"] = upstream.Url + "/v1/";
        _cli.Environment["Providers__fixture__ApiKey"] = "fixture-provider-key";
        _cli.Environment["Providers__fixture__Models__chat__ModelId"] = "upstream/model";
        _cli.Environment["Providers__fixture__Models__chat__MaxOutputTokens"] = "64";
        _cli.Environment["Providers__fixture__Models__chat__Tools"] = "false";
        string[] format = json ? ["--json"] : [];
        string[] command = ["run", "managed smoke"];
        if (chat)
        {
            _cli.FontSetup.ConfigureAsync("auto", Arg.Any<CancellationToken>()).Returns(true);
            _cli.Input = new StringReader("managed smoke\n/exit\n");
            _cli.ConfigureConsole = console =>
            {
                console.Profile.Capabilities.Interactive = !json;
                if (!json)
                {
                    console.Input.PushKey(ConsoleKey.DownArrow);
                    console.Input.PushKey(ConsoleKey.Enter);
                }
            };
            command = json ? ["chat", "--trust-workspace"] : [];
        }

        var (code, output) = await _cli.Run([.. command, "--model", "fixture/chat", .. format]);

        code.ShouldBe(ExitCodes.Success, output);
        if (json)
        {
            var records = Records(output);
            records.First().GetProperty("session").GetProperty("model").GetString().ShouldBe("fixture/chat");
            records.Last().GetProperty("kind").GetString().ShouldBe("turn.completed");
            string.Concat(records.Where(record => record.GetProperty("kind").GetString() == "text.delta")
                .Select(record => record.GetProperty("text").GetString())).ShouldBe("OK");
        }
        else if (chat)
        {
            output.ShouldContain("Workspace access");
            output.ShouldContain(_cli.Files.Locations.WorkingDirectory);
            output.ShouldContain("CodeAssist v");
            output.ShouldContain("fixture/chat");
            output.ShouldContain("permissions: ask");
            output.IndexOf("CodeAssist v", StringComparison.Ordinal).ShouldBeGreaterThan(output.IndexOf("Allow for this launch", StringComparison.Ordinal));
            output.ShouldContain("OK");
        }
        else output.ShouldBe("OK" + Environment.NewLine);
        output.ShouldNotContain("fixture-provider-key");
        var request = upstream.LogEntries.ShouldHaveSingleItem().RequestMessage.ShouldNotBeNull();
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("model").GetString().ShouldBe("upstream/model");
        body.RootElement.GetProperty("stream").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("messages").EnumerateArray().ShouldContain(message =>
            message.GetProperty("role").GetString() == "user" && message.GetProperty("content").GetString() == "managed smoke");
    }

    [Fact]
    public async Task Should_StartActualLauncherAfterProviderRemoval_When_WorkspaceContainsUnavailableSavedSession()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "ca"))) repository = repository.Parent;
        repository.ShouldNotBeNull();
        using var upstream = WireMockServer.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        foreach (var provider in new[] { "retired", "current", "unconfigured" })
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "bash")
            {
                WorkingDirectory = _cli.Files.Work,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (OperatingSystem.IsWindows()) start.ArgumentList.Add("/c");
            start.ArgumentList.Add(Path.Combine(repository.FullName, OperatingSystem.IsWindows() ? "ca.cmd" : "ca"));
            start.ArgumentList.Add("--trust-workspace");
            if (provider != "unconfigured")
            {
                start.ArgumentList.Add("--json");
                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(provider + "/chat");
            }
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Providers__", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment[SettingsLocations.HomeVariable] = _cli.Files.Home;
            start.Environment[SettingsLocations.ManagedVariable] = _cli.Files.Managed;
            start.Environment["PWD"] = _cli.Files.Work;
            start.Environment["Catalog__Refresh"] = "never";
            start.Environment.Remove("OPENAI_API_KEY");
            if (provider != "unconfigured")
            {
                start.Environment[$"Providers__{provider}__Protocol"] = "openai";
                start.Environment[$"Providers__{provider}__Api"] = "chat";
                start.Environment[$"Providers__{provider}__Endpoint"] = upstream.Url + "/v1/";
                start.Environment[$"Providers__{provider}__Models__chat__Tools"] = "false";
            }
            using var process = Process.Start(start).ShouldNotBeNull();
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                await process.StandardInput.WriteLineAsync(provider == "unconfigured" ? "/status\n/session.list\n/exit" : "/exit");
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                var text = await output;
                process.ExitCode.ShouldBe(ExitCodes.Success, text + await errors);
                if (provider == "unconfigured")
                {
                    text.ShouldContain("CodeAssist | Service ready");
                    text.ShouldContain("\"status\": \"ready\"");
                    text.ShouldContain("[]");
                    text.ShouldNotContain("error");
                    continue;
                }
                var selected = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith('{')).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ShouldHaveSingleItem();
                selected.GetProperty("kind").GetString().ShouldBe("session.selected");
                selected.GetProperty("session").GetProperty("model").GetString().ShouldBe(provider + "/chat");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        upstream.LogEntries.ShouldBeEmpty();
    }

    private static JsonElement[] Records(string output) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();

    [Theory]
    [InlineData("--set", "agent.maxTurns=2")]
    [InlineData("--role", "reviewer")]
    [InlineData("--permission-preset", "readOnly")]
    [InlineData("--max-turns", "2")]
    public async Task Should_RejectStartupOverridesBeforeHttp_When_ExternalServiceIsAttached(string option, string value)
    {
        using var api = new ConversationApi(_cli);

        var (code, output) = await _cli.Run("run", "prompt", "--endpoint", api.Url, option, value);

        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("An attached service retains its startup settings.");
        api.Calls.ShouldBeEmpty();
    }

    internal sealed class ConversationApi : IDisposable
    {
        private readonly WireMockServer _server = WireMockServer.Start();
        private readonly string _token = new('t', 40);
        public static AgentSessionDto Session => new(SessionId, "default/regression", CreatedAt, false, 2);
        public static AgentMessageDto[] Messages => [new(1, "user", "earlier", CreatedAt), new(2, "assistant", "[bold]committed[/]", CreatedAt)];
        public static AgentSessionDto Branch => Session with { Id = Guid.Parse("33333333-3333-3333-3333-333333333333") };
        public static AgentSessionArchive Archive => new(1, Session.Model, CreatedAt, Messages, JsonSerializer.SerializeToElement(new { marker = "framework-state" }));
        public string Url => _server.Url!;
        public string[] Calls => _server.LogEntries.Select(entry => $"{entry.RequestMessage!.Method.ToUpperInvariant()} {entry.RequestMessage.Path}").ToArray();

        public ConversationApi(CliHarness cli)
        {
            cli.Environment["Host__Listen__Http__Token"] = _token;
            cli.Environment["ENABLED_LIVE_TESTS"] = "0";
            Json("GET", "/v1/health", new ServerHealthDto("code-assist", "1", "ready"));
            Json("POST", "/v1/sessions", Session, 201);
            Json("GET", "/v1/sessions", new[] { Session });
            Json("GET", $"/v1/sessions/{SessionId}", Session);
            Json("GET", $"/v1/sessions/{SessionId}/messages", Messages);
            Json("GET", $"/v1/sessions/{SessionId}/archive", Archive);
            Json("POST", $"/v1/sessions/{SessionId}/fork", Branch, 201);
            Json("POST", "/v1/sessions/import", Branch, 201);
            Json("DELETE", $"/v1/sessions/{SessionId}", new { }, 204);
            Stream("turn.completed");
        }

        public void Json(string method, string path, object body, int status = 200) =>
            _server.Given(Request.Create().WithPath(path).UsingMethod(method).WithHeader("Authorization", new ExactMatcher("Bearer " + _token)))
                .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(JsonSerializer.Serialize(body, JsonSerializerOptions.Web)));

        public void Raw(string method, string path, string body, string contentType = "application/json", int status = 200, string? location = null)
        {
            var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body);
            if (location is not null) response.WithHeader("Location", location);
            _server.Given(Request.Create().WithPath(path).UsingMethod(method).WithHeader("Authorization", new ExactMatcher("Bearer " + _token))).RespondWith(response);
        }

        public WireMock.IRequestMessage RequestAt(string path) => _server.LogEntries.Single(entry => entry.RequestMessage?.Path == path).RequestMessage!;

        public static AgentTurnEvent[] Events(string terminal)
        {
            AgentTurnEvent[] events = [new(TurnId, 1, "turn.started", null), new(TurnId, 2, "text.delta", "[bold]literal[/] <tag>\n"), new(TurnId, 3, "text.delta", "second line")];
            return terminal == "truncated" ? events : [.. events, new(TurnId, 4, terminal, null)];
        }

        public void Stream(string terminal) => RawStream(string.Concat(Events(terminal).Select(item => $"event: {item.Kind}\ndata: {JsonSerializer.Serialize(item, JsonSerializerOptions.Web)}\n\n")));

        public void RawStream(string body, string contentType = "text/event-stream") =>
            _server.Given(Request.Create().WithPath($"/v1/sessions/{SessionId}/messages/stream").UsingPost()
                .WithHeader("Authorization", new ExactMatcher("Bearer " + _token)).WithHeader("Accept", "text/event-stream"))
                .RespondWith(Response.Create().WithHeader("Content-Type", contentType).WithBody(body));

        public void BlockStream(TaskCompletionSource<SendAgentMessageRequest> entered, TaskCompletionSource release) =>
            _server.Given(Request.Create().WithPath($"/v1/sessions/{SessionId}/messages/stream").UsingPost()
                .WithHeader("Authorization", new ExactMatcher("Bearer " + _token)).WithHeader("Accept", "text/event-stream"))
                .RespondWith(Response.Create().WithCallback(async request =>
                {
                    entered.TrySetResult(JsonSerializer.Deserialize<SendAgentMessageRequest>(request.Body!, JsonSerializerOptions.Web)!);
                    await release.Task;
                    return new WireMock.ResponseMessage { StatusCode = 204 };
                }));

        public T Body<T>(string path) => Bodies<T>(path).Single();
        public CodeAssistClient CreateClient() => new(new Uri(Url), _token);
        public T[] Bodies<T>(string path) => _server.LogEntries.Where(entry => entry.RequestMessage?.Path == path && entry.RequestMessage.Body is not null)
            .Select(entry => JsonSerializer.Deserialize<T>(entry.RequestMessage!.Body!, JsonSerializerOptions.Web)!).ToArray();

        public void ShouldNotLeakToken(string output) => output.Contains(_token, StringComparison.Ordinal).ShouldBeFalse("Service credential must not appear in output.");
        public void Dispose() => _server.Dispose();
    }
}