using CodeAssist.Testing.Live;
using Shouldly;

namespace CodeAssist.Testing.UnitTests.Live;

public sealed class LiveTestConfigurationTests
{
    [Fact]
    public void DotEnvParsesCommentsExportQuotesAndTrailingComments()
    {
        var values = DotEnvFile.Parse("""
            # comment
            PLAIN=value
            export EXPORTED=yes
            QUOTED="a b # not comment"
            SINGLE='raw $value'
            TRAILING=abc # note
            EMPTY=
            Providers__openai-byok__Models__deepseek-flash__Tools=true
            Providers__openai-byok__Models__vendor/model.v1__Name=Model
            Agent="{\"model\":\"openai-byok/deepseek-flash\"}"
            bad line
            1BAD-NAME=x
            """.ReplaceLineEndings("\r\n"));

        values["PLAIN"].ShouldBe("value");
        values["EXPORTED"].ShouldBe("yes");
        values["QUOTED"].ShouldBe("a b # not comment");
        values["SINGLE"].ShouldBe("raw $value");
        values["TRAILING"].ShouldBe("abc");
        values["EMPTY"].ShouldBe("");
        values["Providers__openai-byok__Models__deepseek-flash__Tools"].ShouldBe("true");
        values["Providers__openai-byok__Models__vendor/model.v1__Name"].ShouldBe("Model");
        values["Agent"].ShouldBe("""{"model":"openai-byok/deepseek-flash"}""");
        values.ContainsKey("bad line").ShouldBeFalse();
        values.ContainsKey("1BAD-NAME").ShouldBeFalse();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    public void EnabledFlagAcceptsOneOrTrue(string value, bool expected)
    {
        new LiveTestConfiguration(new Dictionary<string, string> { [LiveTestConfiguration.EnabledVariable] = value }).Enabled.ShouldBe(expected);
    }

    [Fact]
    public void RetiredSwitchDoesNotEnableLiveRequests()
    {
        new LiveTestConfiguration(new Dictionary<string, string> { ["CODEASSIST_LIVE_TESTS"] = "1" }).Enabled.ShouldBeFalse();
    }

    [Fact]
    public void NormalSettingsArePreservedWithoutTestSpecificRemapping()
    {
        var config = new LiveTestConfiguration(new Dictionary<string, string>
        {
            ["Providers__openai-byok__Endpoint"] = "https://api.deepseek.com",
            ["Providers__openai-byok__Models__deepseek-flash__Tools"] = "true",
            ["Agent__Model"] = "openai-byok/deepseek-flash",
        });

        config.Environment["Providers__openai-byok__Endpoint"].ShouldBe("https://api.deepseek.com");
        config.Environment["Providers__openai-byok__Models__deepseek-flash__Tools"].ShouldBe("true");
        config.Environment["Agent__Model"].ShouldBe("openai-byok/deepseek-flash");
    }

    [Fact]
    public void EnvironmentIsSnapshotAndValueTreatsBlankAsAbsent()
    {
        var values = new Dictionary<string, string> { ["A"] = " ", ["B"] = "b" };
        var config = new LiveTestConfiguration(values);
        values["B"] = "changed";

        config.Value("A").ShouldBeNull();
        config.Value("B").ShouldBe("b");
        config.Value("missing").ShouldBeNull();
    }

}
