using CodeAssist.Configuration;
using Shouldly;

namespace CodeAssist.Configuration.UnitTests.Providers;

public sealed class ProviderPresetTests
{
	[Theory]
	[InlineData("google", ProviderProtocol.Gemini)]
	[InlineData("google-vertex", ProviderProtocol.Vertex)]
	[InlineData("amazon-bedrock", ProviderProtocol.Bedrock)]
	public void NativePresetsUseNativeProtocols(string id, ProviderProtocol protocol)
	{
		ProviderPresets.Find(id)!.ToSettings().Protocol.ShouldBe(protocol);
	}

	[Theory]
	[InlineData("google-vertex")]
	[InlineData("amazon-bedrock")]
	public void CloudProvidersCanUseAmbientCredentials(string id)
	{
		var status = ProviderInspector.Inspect(id, ProviderPresets.Find(id)!.ToSettings(), new Dictionary<string, string>());

		status.Source.ShouldBe(CredentialSource.CloudIdentity);
		status.Usable.ShouldBeTrue();
	}

	[Fact]
	public void CatalogMetadataCanSupplyEndpointAndKeyNames()
	{
		var status = ProviderInspector.Inspect("acme", new ProviderSettings(),
			new Dictionary<string, string> { ["ACME_API_KEY"] = "secret" }, "https://api.acme.example", ["ACME_API_KEY"]);

		status.Usable.ShouldBeTrue();
		status.Endpoint.ShouldBe("https://api.acme.example");
		status.SourceDetail.ShouldBe("ACME_API_KEY");
	}
}
