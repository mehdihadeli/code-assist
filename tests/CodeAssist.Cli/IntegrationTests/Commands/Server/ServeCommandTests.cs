using CodeAssist.Cli.Hosting;
using CodeAssist.Cli.IntegrationTests.Support;
using Shouldly;

namespace CodeAssist.Cli.IntegrationTests.Commands.Server;

public sealed class ServeCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();
    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Should_ShowServeHelp_When_CommandIsRegistered()
    {
        var (code, output) = await _cli.Run("serve", "--help");
        code.ShouldBe(0, output);
        output.ShouldContain("--listen");
    }

    [Fact]
    public async Task Should_RefuseToStart_When_AuthenticationTokenIsMissing()
    {
        var (code, output) = await _cli.Run("serve");
        code.ShouldBe(ExitCodes.UsageOrConfig);
        output.ShouldContain("Host__Listen__Http__Token");
    }

    [Fact]
    public async Task Should_RedactTokenAndApplyIndexedOrigins_When_SettingsComeFromEnvironment()
    {
        _cli.Environment["Host__Listen__Http__Token"] = "test-token-must-never-be-shown-123456789";
        _cli.Environment["Host__Listen__Http__AllowedOrigins__0"] = "http://localhost:3000";
        var (code, output) = await _cli.Run("config", "show", "--json");
        code.ShouldBe(0, output);
        output.ShouldNotContain("test-token-must-never-be-shown-123456789");
        output.ShouldContain("http://localhost:3000");
    }

    [Fact]
    public async Task Should_RejectRemoteBindingWithoutLeakingToken_When_ServeIsRequested()
    {
        _cli.Environment["Host__Listen__Http__Token"] = "test-token-must-never-be-shown-123456789";
        var (code, output) = await _cli.Run("serve", "--listen", "0.0.0.0:8080");
        code.ShouldNotBe(0);
        output.ShouldContain("loopback");
        output.ShouldNotContain("test-token-must-never-be-shown-123456789");
    }
}