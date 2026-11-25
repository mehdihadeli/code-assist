using CodeAssist.Tools;
using Shouldly;

namespace CodeAssist.Sessions.Domain.UnitTests.Tools;

public sealed class ToolArgumentsTests
{
    [Fact]
    public void ToolArgumentsReportAtMostFiveIssues()
    {
        var args = ToolArguments.Parse("""{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6,"g":7}""");
        foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "g" }) args.String(name);

        var exception = Should.Throw<ToolArgumentException>(args.Validate);

        exception.Issues.Count.ShouldBe(7);
        exception.Message.ShouldContain("and 2 more");
    }

    [Fact]
    public void ToolArgumentsPrefixNestedIssues()
    {
        var args = ToolArguments.Parse("""{"items":[{"x":1}]}""");
        args.Objects("items")[0].RequireString("x");

        Should.Throw<ToolArgumentException>(args.Validate).Message.ShouldContain("items[0].x");
    }
}