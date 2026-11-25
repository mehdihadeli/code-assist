using System.Net;
using CodeAssist.Models.Catalog;
using Shouldly;
using System.Text.Json.Nodes;

namespace CodeAssist.Models.IntegrationTests.Catalog;

public sealed class ModelCatalogTests : IDisposable
{
    private const string Feed = """
    {
      "acme": {
        "id": "acme", "name": "Acme", "extra": 1,
        "models": {
          "fast-1": {
            "id": "fast-1", "name": "Fast One", "tool_call": true, "reasoning": false,
            "modalities": { "input": ["text", "image"], "output": ["text"] },
            "limit": { "context": 128000, "output": 8192 },
            "cost": { "input": 0.5, "output": 1.5, "cache_read": 0.05 },
            "status": "beta"
          },
          "broken": 42
        }
      },
      "empty": { "name": "no models" }
    }
    """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ca-catalog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void ParseMapsLimitsModalitiesAndPricesAndSkipsNonModelEntries()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse(Feed));

        catalog.Count.ShouldBe(1);
        var model = catalog.Find("acme", "fast-1");
        model.ShouldNotBeNull();
        model.Name.ShouldBe("Fast One");
        model.ContextWindow.ShouldBe(128000);
        model.MaxOutputTokens.ShouldBe(8192);
        model.ToolCalls.ShouldBe(true);
        model.Reasoning.ShouldBeFalse();
        model.Input.ShouldBe(new[] { "text", "image" });
        model.Output.ShouldBe(new[] { "text" });
        model.Price!.Input.ShouldBe(0.5m);
        model.Price.CacheRead.ShouldBe(0.05m);
        model.Price.CacheWrite.ShouldBeNull();
        model.Status.ShouldBe("beta");
        catalog.Find("acme", "broken").ShouldBeNull();
    }

    [Fact]
    public async Task RefreshWritesCacheAndRevalidatesWithEtag()
    {
        string? sentIfNoneMatch = null;
        var handler = new StubHandler(request =>
        {
            sentIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            if (sentIfNoneMatch is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed, System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        });
        var client = new ModelCatalogClient(new HttpClient(handler));
        var url = "https://models.example/api.json";

        var first = await client.RefreshAsync(url, _dir, TestContext.Current.CancellationToken);
        first.Status.ShouldBe(CatalogRefreshStatus.Updated);
        first.ModelCount.ShouldBe(1);
        File.Exists(Path.Combine(_dir, ModelCatalogClient.FeedFileName)).ShouldBeTrue();

        var second = await client.RefreshAsync(url, _dir, TestContext.Current.CancellationToken);
        second.Status.ShouldBe(CatalogRefreshStatus.NotModified);
        sentIfNoneMatch.ShouldBe("\"v1\"");

        ModelCatalogClient.LoadCache(_dir).Catalog.Find("acme", "fast-1").ShouldNotBeNull();
    }

    [Fact]
    public async Task UnreadableCacheDoesNotSendAnEtag()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, ModelCatalogClient.FeedFileName), "broken", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_dir, ModelCatalogClient.ETagFileName), "\"old\"", TestContext.Current.CancellationToken);
        var client = new ModelCatalogClient(new HttpClient(new StubHandler(request =>
        {
            request.Headers.IfNoneMatch.ShouldBeEmpty();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) };
        })));

        var result = await client.RefreshAsync("https://models.example/api.json", _dir, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CatalogRefreshStatus.Updated);
        ModelCatalogClient.LoadCache(_dir).Catalog.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FailedFetchKeepsPreviousCache()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, ModelCatalogClient.FeedFileName), Feed, TestContext.Current.CancellationToken);
        var client = new ModelCatalogClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        var result = await client.RefreshAsync("https://models.example/api.json", _dir, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CatalogRefreshStatus.Failed);
        result.Error!.ShouldContain("500");
        ModelCatalogClient.LoadCache(_dir).Catalog.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RejectsNonHttpsUrlsWithoutSendingRequests()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var client = new ModelCatalogClient(new HttpClient(handler));

        var result = await client.RefreshAsync("http://models.example/api.json", _dir, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CatalogRefreshStatus.Failed);
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task RejectsFeedWithoutModels()
    {
        var client = new ModelCatalogClient(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"acme\":{\"name\":\"x\"}}") })));

        var result = await client.RefreshAsync("https://models.example/api.json", _dir, TestContext.Current.CancellationToken);

        result.Status.ShouldBe(CatalogRefreshStatus.Failed);
        File.Exists(Path.Combine(_dir, ModelCatalogClient.FeedFileName)).ShouldBeFalse();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
