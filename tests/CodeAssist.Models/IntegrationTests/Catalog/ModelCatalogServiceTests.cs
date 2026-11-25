using CodeAssist.Models.Catalog;
using CodeAssist.Models.Chat;
using System.Net;
using Shouldly;

namespace CodeAssist.Models.IntegrationTests.Catalog;

public sealed class ModelCatalogServiceTests : IDisposable
{
    private const string Feed =
        """
        {"acme":{"id":"acme","models":{
          "large":{"name":"Large","limit":{"context":200000,"output":8000},"tool_call":true,"cost":{"input":3,"output":15}},
          "small":{"name":"Small","limit":{"context":32000,"output":4000}}}}}
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ca-catalog-svc-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void ListAndFindServeTheCachedFeed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, ModelCatalogClient.FeedFileName), Feed);
        var service = new ModelCatalogService(new ModelCatalogClient(new HttpClient()), _directory, () => "https://models.example/api.json");

        service.List("acme").Select(m => m.ModelId).ShouldBe(new[] { "large", "small" });
        service.List("other").ShouldBeEmpty();

        var large = service.Find("acme", "large");
        large.ShouldNotBeNull();
        large.ContextWindow.ShouldBe(200000);
        large.Price!.Output.ShouldBe(15m);
        service.Find("acme", "missing").ShouldBeNull();
    }

    [Fact]
    public void ListsCatalogProvidersAndOnlyActiveTextModels()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, ModelCatalogClient.FeedFileName), """
            {"acme":{"name":"Acme","protocol":"anthropic","api":"https://api.acme.example","env":["ACME_API_KEY"],
              "models":{"old":{"status":"deprecated"},"image":{"modalities":{"output":["image"]}},
                "active":{"temperature":false,"limit":{"input":32000},"reasoning_options":[{"type":"effort","values":["high"]}],
                  "cost":{"input":1,"tiers":[{"tier":{"type":"context","size":16000},"input":2}]}}}}}
            """);
        var service = new ModelCatalogService(new ModelCatalogClient(new HttpClient()), _directory, () => "https://models.example/api.json");

        service.ListProviders().ShouldHaveSingleItem().Env.ShouldBe(["ACME_API_KEY"]);
        var model = service.List().ShouldHaveSingleItem();
        model.ModelId.ShouldBe("active");
        model.InputTokenLimit.ShouldBe(32000);
        model.Temperature.ShouldBe(false);
        model.ToolCallSupport.ShouldBeNull();
        model.ReasoningEfforts.ShouldBe(["high"]);
        model.Price!.Tiers.ShouldHaveSingleItem().Price.Input.ShouldBe(2m);
        service.Find("acme", "old").ShouldNotBeNull();
    }

    [Fact]
    public async Task ExistingRegistryUsesRefreshedProviderAndModelMetadata()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, ModelCatalogClient.FeedFileName), """
            {"acme":{"protocol":"anthropic","api":"https://old.acme.example","env":["OLD_KEY"],"models":{"m":{"limit":{"output":8192}}}}}
            """);
        using var http = new HttpClient(new RefreshHandler());
        var service = new ModelCatalogService(new ModelCatalogClient(http), _directory, () => "https://models.example/api.json");
        var registry = ProviderRegistry.CreateDefault(service.FindProvider, service.FindModel);
        var connection = new ProviderConnection { Id = "acme" };
        registry.Resolve(connection, "m").Connection.Wire.ShouldBe(WireProtocol.Anthropic);

        var refresh = await service.RefreshAsync(TestContext.Current.CancellationToken);
        var resolved = registry.Resolve(connection, "m").Connection;

        refresh.Status.ShouldBe("Updated");
        resolved.Wire.ShouldBe(WireProtocol.OpenAiChat);
        resolved.Endpoint!.Host.ShouldBe("new.acme.example");
        resolved.MaxOutputTokens.ShouldBe(1024);
        registry.KeyVariables("acme").ShouldBe(["NEW_KEY"]);
    }

    private sealed class RefreshHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"acme":{"protocol":"chat","api":"https://new.acme.example/v1","env":["NEW_KEY"],"models":{"m":{"limit":{"output":1024}}}}}
                    """),
            });
    }
}
