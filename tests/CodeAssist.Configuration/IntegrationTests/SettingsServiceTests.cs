using System.Text.Json.Nodes;
using CodeAssist.Configuration;
using Shouldly;

using CodeAssist.Configuration.IntegrationTests.Support;

namespace CodeAssist.Configuration.IntegrationTests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly SettingsFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private SettingsService NewService() =>
        new(_fx.Locations, new DictionaryEnvironmentVariables(new Dictionary<string, string>()), new FlagOverrides());

    [Fact]
    public void InvalidReloadKeepsLastGoodSnapshot()
    {
        _fx.User("""{ "agent": { "maxTurns": 7 } }""");
        var service = NewService();
        service.Current.Settings.Agent.MaxTurns.ShouldBe(7);
        var rejected = 0;
        service.Rejected += _ => rejected++;

        _fx.User("{ broken");
        var result = service.Reload();

        result.Accepted.ShouldBeFalse();
        rejected.ShouldBe(1);
        service.Current.Settings.Agent.MaxTurns.ShouldBe(7);

        _fx.User("""{ "agent": { "maxTurns": 9 } }""");
        service.Reload().Changed.ShouldBeTrue();
        service.Current.Settings.Agent.MaxTurns.ShouldBe(9);
    }

    [Fact]
    public void FlagOverridesApplyOnReload()
    {
        var service = NewService();
        service.Flags.Set("agent.maxTurns", "2");

        service.Reload();

        service.Current.Settings.Agent.MaxTurns.ShouldBe(2);
    }
}

