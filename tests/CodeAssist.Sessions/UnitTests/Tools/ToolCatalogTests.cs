using CodeAssist.Tools;
using CodeAssist.Contracts;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Sessions.Domain.UnitTests.Tools;

public sealed class ToolCatalogTests
{
    [Theory]
    [InlineData("", "{}")]
    [InlineData("bad name", "{}")]
    [InlineData("plugin.read", "[]")]
    [InlineData("plugin.read", "{")]
    public void Should_RejectInvalidDescriptors_When_CustomToolIsRegistered(string name, string schema)
    {
        var tool = Substitute.For<ITool>();
        tool.Spec.Returns(new ToolSpec(name, "Read a plugin value.", schema, ToolRisk.Read, ["read"]));

        Should.Throw<ArgumentException>(() => new ToolCatalog([tool]));
    }

    [Fact]
    public void Should_ValidateNestedConstraintsAndLocalReferences_When_InputIsChecked()
    {
        var schema = new ToolInputSchema("""
            {"type":"object","properties":{"values":{"type":"array","minItems":1,"items":{"$ref":"#/$defs/positive"}}},
             "required":["values"],"additionalProperties":false,"$defs":{"positive":{"type":"integer","minimum":1}}}
            """);
        schema.Validate(ToolArguments.Parse("""{"values":[1,2]}""").Json);
        Should.Throw<ToolArgumentException>(() => schema.Validate(ToolArguments.Parse("""{"values":[0]}""").Json));
        Should.Throw<ToolArgumentException>(() => schema.Validate(ToolArguments.Parse("""{"values":[],"extra":true}""").Json));
        Should.Throw<ArgumentException>(() => new ToolInputSchema("""{"$ref":"https://example.invalid/schema"}"""));
    }

    [Fact]
    public void Should_SnapshotSourcesAndRejectCollisions_When_CustomToolsAreRegistered()
    {
        var tool = Substitute.For<ITool>();
        tool.Spec.Returns(new ToolSpec("acme.lookup", "Look up a value.", "{}", ToolRisk.Read, ["read", "acme"]));
        var tools = new List<ITool> { tool };
        var source = new ToolSource(tools);
        tools.Clear();

        var catalog = new ToolCatalog(source.Tools);

        catalog.Find("acme.lookup").ShouldBeSameAs(tool);
        catalog.Visible(["read"], ["acme"]).ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => new ToolCatalog([tool, tool]));
        Should.Throw<NotSupportedException>(() => ((IList<ITool>)catalog.All).Clear());
    }
}