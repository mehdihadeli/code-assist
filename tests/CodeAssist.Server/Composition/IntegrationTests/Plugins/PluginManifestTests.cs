using System.Text.Json;
using CodeAssist.Extensions.Plugins;
using Shouldly;

namespace CodeAssist.Server.Composition.IntegrationTests.Plugins;

public sealed class PluginManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "code-assist-plugin-" + Guid.NewGuid().ToString("N"));

    public PluginManifestTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Should_ReadExplicitContributions_When_ManifestIsValid()
    {
        Write(new PluginManifest { Id = "team.review", Release = "1.2.0", Summary = "Review guidance", Contributions =
            [new() { Key = "inspect", Kind = "skill", Entry = "guidance/inspect.md", Description = "Inspect a change" }] });
        var manifest = PluginManifest.Read(_root);
        manifest.Id.ShouldBe("team.review");
        manifest.Contributions.Single().Entry.ShouldBe("guidance/inspect.md");
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("/outside.md")]
    [InlineData("C:/outside.md")]
    [InlineData("guidance\\outside.md")]
    [InlineData("guidance/CON.md")]
    [InlineData("guidance/../outside.md")]
    [InlineData("guidance/hidden./outside.md")]
    public void Should_RejectUnsafePaths_When_ContributionEscapesPortableLayout(string path)
    {
        Write(new PluginManifest { Id = "team.review", Release = "1.0.0", Summary = "Review", Contributions =
            [new() { Key = "inspect", Kind = "skill", Entry = path, Description = "Inspect" }] });
        Should.Throw<InvalidDataException>(() => PluginManifest.Read(_root));
    }

    [Theory]
    [InlineData("{\"schema\":2,\"id\":\"team.review\",\"release\":\"1.0.0\",\"summary\":\"Review\"}")]
    [InlineData("{\"id\":\"team.review\",\"release\":\"latest\",\"summary\":\"Review\"}")]
    [InlineData("{\"id\":\"team.review\",\"release\":\"1.0.0\",\"summary\":\"Review\",\"install\":\"run me\"}")]
    [InlineData("{\"id\":\"team.review\",\"id\":\"other.review\",\"release\":\"1.0.0\",\"summary\":\"Review\"}")]
    [InlineData("{\"id\":\"team.review\",\"release\":\"1.0.0\",\"summary\":\"Review\",\"contributions\":null}")]
    public void Should_RejectAmbiguousManifests_When_SchemaOrPropertiesAreInvalid(string json)
    {
        File.WriteAllText(Path.Combine(_root, PluginManifest.FileName), json);
        Should.Throw<InvalidDataException>(() => PluginManifest.Read(_root));
    }

    [Fact]
    public void Should_RejectDuplicateKeys_When_ContributionsCollide()
    {
        Write(new PluginManifest { Id = "team.review", Release = "1.0.0", Summary = "Review", Contributions =
        [
            new() { Key = "inspect", Kind = "skill", Entry = "one.md", Description = "One" },
            new() { Key = "inspect", Kind = "prompt", Entry = "two.md", Description = "Two" },
        ] });
        Should.Throw<InvalidDataException>(() => PluginManifest.Read(_root));
    }

    private void Write(PluginManifest manifest) => File.WriteAllText(Path.Combine(_root, PluginManifest.FileName),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public void Dispose() => Directory.Delete(_root, true);
}