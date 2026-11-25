using Shouldly;

namespace CodeAssist.Configuration.UnitTests.Providers;

public sealed class EnvironmentReferenceTests
{
    [Theory]
    [InlineData("{env:DEEPSEEK_API_KEY}", "DEEPSEEK_API_KEY")]
    [InlineData("{env:Providers__my-route/model__ApiKey}", "Providers__my-route/model__ApiKey")]
    [InlineData("{env:_KEY}", "_KEY")]
    public void Should_ParseWholeReference_When_VariableNameIsValid(string reference, string variable)
    {
        EnvironmentReference.TryParse(reference, out var actual).ShouldBeTrue();
        actual.ShouldBe(variable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("literal-secret")]
    [InlineData("prefix{env:KEY}")]
    [InlineData("{env:KEY}suffix")]
    [InlineData("{env:}")]
    [InlineData("{env: KEY}")]
    [InlineData("{env:KEY\n}")]
    [InlineData("{env:KEY=secret}")]
    [InlineData("{env:{env:KEY}}")]
    [InlineData("{ENV:KEY}")]
    public void Should_RejectInterpolationOrLiteral_When_ValueIsNotWholeReference(string? reference)
    {
        EnvironmentReference.TryParse(reference, out var variable).ShouldBeFalse();
        variable.ShouldBeEmpty();
    }
}