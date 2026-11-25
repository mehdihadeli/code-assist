using CodeAssist.Testing.Live;
using Shouldly;

namespace CodeAssist.Testing.IntegrationTests.Live;

public sealed class LiveTestConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void LoadReadsRootEnvFileAndProcessEnvironmentWins()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;
        File.WriteAllText(Path.Combine(_root, "CodeAssist.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, ".env"), "ENABLED_LIVE_TESTS=1\nGROQ_API_KEY=from-file\nONLY_FILE=1\n");

        var config = LiveTestConfiguration.Load(new Dictionary<string, string> { ["GROQ_API_KEY"] = "from-env" }, nested);

        config.Enabled.ShouldBeTrue();
        config.Value("GROQ_API_KEY").ShouldBe("from-env");
        config.Value("ONLY_FILE").ShouldBe("1");
    }

    [Fact]
    public void LiveTestsAreOffByDefault()
    {
        LiveTestConfiguration.Load(new Dictionary<string, string>(), _root).Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("flase", false)]
    public void Should_RespectLiveTestFlag_When_ValueIsProvided(string value, bool expected)
    {
        var configuration = new LiveTestConfiguration(new Dictionary<string, string>
        {
            [LiveTestConfiguration.EnabledVariable] = value,
        });

        configuration.Enabled.ShouldBe(expected);
    }

    [Fact]
    public void ProcessCanDisableLiveTestsAndClearDotEnvSettings()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "CodeAssist.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, ".env"), "ENABLED_LIVE_TESTS=1\nProviders__openai-byok__Endpoint=https://api.deepseek.com\nProviders__openai-byok__ApiKey=fixture\n");

        var configuration = LiveTestConfiguration.Load(new Dictionary<string, string>
        {
            ["ENABLED_LIVE_TESTS"] = "false",
            ["Providers__openai-byok__ApiKey"] = "",
        }, _root);

        configuration.Enabled.ShouldBeFalse();
        configuration.Environment["Providers__openai-byok__ApiKey"].ShouldBe("");
        configuration.Environment["Providers__openai-byok__Endpoint"].ShouldBe("https://api.deepseek.com");
        configuration.RepositoryDirectory.ShouldBe(_root);
    }
}