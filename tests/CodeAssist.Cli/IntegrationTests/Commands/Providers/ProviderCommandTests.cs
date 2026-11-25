using CodeAssist.Cli.IntegrationTests.Support;

using CodeAssist.Cli.Hosting;
using System.Text.Json.Nodes;
using Shouldly;

namespace CodeAssist.Cli.IntegrationTests.Commands.Providers;

public sealed class ProviderCommandTests : IDisposable
{
    private readonly CliHarness _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Should_WriteOnlyReferencesAndMaskErrors_When_EditingProviderCredential()
    {
        var rejected = await _cli.Run("config", "set", "providers.default.apiKey", "fixture-sensitive-value");
        rejected.Code.ShouldNotBe(0);
        rejected.Output.ShouldNotContain("fixture-sensitive-value");
        var accepted = await _cli.Run("config", "set", "providers.default.apiKey", "{env:CUSTOM_KEY}");
        accepted.Code.ShouldBe(0, accepted.Output);
        _cli.UserJson()["providers"]!["default"]!["apiKey"]!.GetValue<string>().ShouldBe("{env:CUSTOM_KEY}");
    }

    [Fact]
    public async Task CatalogProviderCanBeAddedWithoutEndpointAndWithoutSavingSecrets()
    {
        SeedCatalog();
        _cli.Environment["ACME_API_KEY"] = "secret-acme";

        var (code, output) = await _cli.Run("providers", "add", "acme", "--default-model", "m");

        code.ShouldBe(0, output);
        output.ShouldContain("Ready");
        var saved = _cli.UserJson()["providers"]!["acme"]!;
        saved["protocol"]!.GetValue<string>().ShouldBe("auto");
        saved["endpoint"].ShouldBeNull();
        saved["apiKey"]!.GetValue<string>().ShouldBe("{env:ACME_API_KEY}");
        saved["envs"].ShouldBeNull();
        _cli.UserJson().ToJsonString().ShouldNotContain("secret-acme");
        (await _cli.Run("providers", "add", "acme")).Code.ShouldBe(ExitCodes.UsageOrConfig);

        var (_, list) = await _cli.Run("providers", "list", "--json");
        var status = JsonNode.Parse(list)!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "acme")!;
        status["endpoint"]!.GetValue<string>().ShouldBe("https://api.acme.example");
        status["usable"]!.GetValue<bool>().ShouldBeTrue();
        (await _cli.Run("doctor")).Code.ShouldBe(0);
    }

    [Fact]
    public async Task CatalogProviderDiscoveryUsesCachedMetadata()
    {
        SeedCatalog();

        var (code, output) = await _cli.Run("providers", "presets", "--catalog", "--json");

        code.ShouldBe(0, output);
        var provider = JsonNode.Parse(output)!.AsArray().ShouldHaveSingleItem()!;
        provider["id"]!.GetValue<string>().ShouldBe("acme");
        provider["protocol"]!.GetValue<string>().ShouldBe("anthropic");
    }

    private void SeedCatalog()
    {
        var directory = Path.Combine(_cli.Files.Locations.StateDirectory, "catalog");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "catalog.json"), """
            {"acme":{"name":"Acme","protocol":"anthropic","api":"https://api.acme.example","env":["ACME_API_KEY"],"models":{"m":{}}}}
            """);
    }
}
