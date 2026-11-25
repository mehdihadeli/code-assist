using CodeAssist.Models;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Workspaces.Platform;
using OpenAI;
using OpenAI.Images;
using System.ClientModel;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class MediaRenderTests
{
    [Fact]
    public async Task Should_ReturnImageBytesAndRejectUrlFetch_When_UsingOfficialImageSdk()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/v1/images/generations").UsingPost()
                .WithBody(new JsonPartialMatcher("""{"prompt":"an icon"}""")))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"created":1,"data":[{"b64_json":"iVBORw=="}]}"""));
        server.Given(Request.Create().WithPath("/v1/images/generations").UsingPost()
                .WithBody(new JsonPartialMatcher("""{"prompt":"URL-only icon"}""")))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { created = 1, data = new[] { new { url = server.Url + "/untrusted.png" } } })));
        var client = new ImageClient("gpt-image-1", new ApiKeyCredential("fixture"), new OpenAIClientOptions { Endpoint = new(server.Url + "/v1/") });
        var renderer = new OpenAiImageRenderer(new Dictionary<string, ImageClient> { ["test/gpt-image-1"] = client });
        renderer.Models.Single().CanEdit.ShouldBeFalse();
        var result = await renderer.RenderAsync(new("test/gpt-image-1", "an icon", []), TestContext.Current.CancellationToken);
        result.Data.ShouldBe(new byte[] { 137, 80, 78, 71 });
        using var request = JsonDocument.Parse(server.LogEntries.Single().RequestMessage.ShouldNotBeNull().Body!);
        request.RootElement.GetProperty("model").GetString().ShouldBe("gpt-image-1");
        server.FindLogEntries(Request.Create().WithHeader("Authorization", "Bearer fixture")).ShouldHaveSingleItem();

        await Should.ThrowAsync<InvalidOperationException>(() => renderer.RenderAsync(new("test/gpt-image-1", "URL-only icon", []), TestContext.Current.CancellationToken));

        server.LogEntries.Count.ShouldBe(2);
        server.FindLogEntries(Request.Create().UsingGet()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_SaveImageAndPreventOverwrite_When_MediaJobCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            var renderer = Substitute.For<IImageRenderer>();
            renderer.Models.Returns([new ImageModel("test/image", true)]);
            renderer.RenderAsync(Arg.Any<ImageRenderRequest>(), Arg.Any<CancellationToken>()).Returns(new RenderedImage("image/png", png));
            await using var jobs = new InMemoryToolJobs();
            var done = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
            jobs.Completed += item => done.TrySetResult(item);
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid() };
            var tool = new MediaRenderTool(renderer, jobs, new LocalFileSystem());
            var args = ToolArguments.Parse("""{"prompt":"an icon","path":"icon.png"}""");
            var plan = await tool.PlanAsync(args, context, TestContext.Current.CancellationToken);
            plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Net);
            plan.Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Write);
            (await tool.ExecuteAsync(args, context, TestContext.Current.CancellationToken)).IsError.ShouldBeFalse();
            var completed = await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            completed.Status.ShouldBe(ToolJobStatus.Completed);
            completed.Result!.Attachments!.Single().Data.ShouldBe(png);
            (await File.ReadAllBytesAsync(Path.Combine(root, "icon.png"), TestContext.Current.CancellationToken)).ShouldBe(png);
            (await tool.ExecuteAsync(args, context, TestContext.Current.CancellationToken)).IsError.ShouldBeTrue();
            await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse("""{"prompt":"icon","model":"missing"}"""), context, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }
}