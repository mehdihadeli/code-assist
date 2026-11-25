using Shouldly;

namespace CodeAssist.Configuration.UnitTests.Providers;

public sealed class EnvironmentCredentialTests
{
    [Theory]
    [InlineData("{env:DEEPSEEK_API_KEY}", true)]
    [InlineData("{env:_KEY1}", true)]
    [InlineData("prefix{env:KEY}", false)]
    [InlineData("{env:KEY}\n", false)]
    [InlineData("{env:}", false)]
    [InlineData("{env:KEY=VALUE}", false)]
    [InlineData("literal-secret", false)]
    public void Should_AcceptOnlyExactReferences_When_ParsingCredential(string value, bool expected) =>
        EnvironmentCredential.TryGetVariable(value, out _).ShouldBe(expected);

    [Fact]
    public void Should_ResolveOnceWithoutMutation_When_ReferencedVariableExists()
    {
        var environment = new Dictionary<string, string> { ["KEY"] = "fixture-secret" };
        EnvironmentCredential.Resolve("{env:KEY}", environment).ShouldBe("fixture-secret");
        environment["KEY"].ShouldBe("fixture-secret");
        EnvironmentCredential.Resolve("direct-environment-secret", environment).ShouldBe("direct-environment-secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{env:OTHER}")]
    public void Should_FailSafely_When_ReferenceIsMissingEmptyOrRecursive(string? value)
    {
        var environment = new Dictionary<string, string>();
        if (value is not null) environment["KEY"] = value;
        var error = Should.Throw<InvalidOperationException>(() => EnvironmentCredential.Resolve("{env:KEY}", environment));
        error.Message.ShouldNotContain("OTHER");
    }
}