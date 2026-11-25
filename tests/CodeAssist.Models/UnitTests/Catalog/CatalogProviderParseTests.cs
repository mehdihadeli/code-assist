using CodeAssist.Models.Catalog;
using Shouldly;
using System.Text.Json.Nodes;

namespace CodeAssist.Models.UnitTests.Catalog;

public sealed class CatalogProviderParseTests
{
	[Fact]
	public void CatalogParsesProviderEntries()
	{
		var catalog = ModelCatalog.Parse(JsonNode.Parse("""
			{
			  "newco": { "id": "newco", "name": "NewCo", "api": "https://api.newco.example/v1", "env": ["NEWCO_API_KEY"], "protocol": "chat", "doc": "https://docs.example", "models": {} },
			  "bare": { "name": "Bare" }
			}
			"""));

		var entry = catalog.FindProvider("newco").ShouldNotBeNull();
		entry.Name.ShouldBe("NewCo");
		entry.Api.ShouldBe("https://api.newco.example/v1");
		entry.Env.ShouldBe(["NEWCO_API_KEY"]);
		entry.Protocol.ShouldBe("chat");
		catalog.FindProvider("bare")!.Api.ShouldBeNull();
		catalog.FindProvider("missing").ShouldBeNull();
	}

	[Fact]
	public void ParsesProviderAndModelRoutingAndCapabilities()
	{
		var catalog = ModelCatalog.Parse(JsonNode.Parse("""
			{"acme":{"name":"Acme","protocol":"chat","api":"https://api.acme.example/v1",
			  "env":["ACME_API_KEY"],"models":{"m":{"tool_call":false,"temperature":false,"attachment":true,
				"limit":{"context":100000,"input":80000,"output":4096},
				"provider":{"protocol":"responses","api":"https://models.acme.example/v1","shape":"responses"},
				"reasoning_options":[{"type":"effort","values":[null,"null","low","high","high"]}],
				"cost":{"input":1,"output":2,"tiers":[{"tier":{"type":"context","size":32000},"input":3,"cache_read":0.5}]}
			  }}}}
			"""));

		catalog.FindProvider("acme")!.Env.ShouldBe(["ACME_API_KEY"]);
		var model = catalog.Find("acme", "m")!;
		model.Provider.ShouldBe(new CatalogRoute("responses", "https://models.acme.example/v1", "responses"));
		model.InputTokenLimit.ShouldBe(80000);
		model.ToolCalls.ShouldBe(false);
		model.Temperature.ShouldBe(false);
		model.Attachment.ShouldBe(true);
		model.ReasoningEfforts.ShouldBe(["low", "high"]);
		var tier = model.Price!.Tiers.ShouldHaveSingleItem();
		tier.AboveTokens.ShouldBe(32000);
		tier.Price.Input.ShouldBe(3m);
		tier.Price.CacheRead.ShouldBe(0.5m);
	}

	[Fact]
	public void MissingCapabilitiesRemainUnknownAndMalformedEntriesAreSkipped()
	{
		var catalog = ModelCatalog.Parse(JsonNode.Parse("""
			{"broken":42,"acme":{"models":{"m":{"limit":42,"modalities":42},"bad":false}}}
			"""));

		catalog.Count.ShouldBe(1);
		var model = catalog.Find("acme", "m")!;
		model.ToolCalls.ShouldBeNull();
		model.Temperature.ShouldBeNull();
		model.ContextWindow.ShouldBeNull();
		model.Input.ShouldBeEmpty();
	}
}
