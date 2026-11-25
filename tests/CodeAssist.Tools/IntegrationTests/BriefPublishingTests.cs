using CodeAssist.Tools;
using CodeAssist.Tools.Extensions;
using CodeAssist.Workspaces.Platform;
using NSubstitute;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class BriefPublishingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_BoundAndDiscloseAttachmentUploads_When_SharingWorkspaceFiles(bool withinBudget)
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-brief-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "report.txt"), withinBudget ? "DATA" : "TOO-LARGE", TestContext.Current.CancellationToken);
            using var server = WireMockServer.Start();
            server.Given(Request.Create().WithPath("/briefs").UsingPost()).RespondWith(Response.Create().WithBody("""{"id":"report-1","url":"https://reports.example/report-1"}"""));
            using var publisher = new HttpBriefPublisher(new Uri(server.Url! + "/briefs"), true);
            var interaction = Substitute.For<IUserInteraction>();
            interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                call.ArgAt<IReadOnlyList<Question>>(0).Single().Text.ShouldContain("Exact report content");
                call.ArgAt<IReadOnlyList<Question>>(0).Single().Text.ShouldContain("report.txt");
                return new[] { new QuestionAnswer(["Publish"], null) };
            });
            var tool = new BriefPublishTool(publisher, interaction, new LocalFileSystem());
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid(), CallId = "brief-files", Limits = new() { MaxAttachmentBytes = 4 } };
            var result = await tool.ExecuteAsync(ToolArguments.Parse("""{"title":"Report","text":"Exact report content","attachments":["report.txt"]}"""), context, TestContext.Current.CancellationToken);
            result.IsError.ShouldBe(!withinBudget);
            server.LogEntries.Count().ShouldBe(withinBudget ? 1 : 0);
            if (withinBudget) server.LogEntries.Single().RequestMessage.ShouldNotBeNull().Body.ShouldNotBeNull().ShouldContain("DATA");
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_UploadOnlyAfterTrustedConsent_When_PublishingReports(bool consent)
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/briefs").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"id":"report-1","url":"https://reports.example/report-1"}"""));
        using var publisher = new HttpBriefPublisher(new Uri(server.Url! + "/briefs"), true);
        var interaction = Substitute.For<IUserInteraction>();
        interaction.AskAsync(Arg.Any<IReadOnlyList<Question>>(), Arg.Any<ToolContext>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new QuestionAnswer([consent ? "Publish" : "Cancel"], null) });
        var fileSystem = Substitute.For<IFileSystem>();
        var tool = new BriefPublishTool(publisher, interaction, fileSystem);
        var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid(), CallId = "brief-1" };
        var input = ToolArguments.Parse("""{"title":"Review results","text":"All checks passed."}""");
        (await tool.PlanAsync(input, context, TestContext.Current.CancellationToken)).Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Ask);
        var result = await tool.ExecuteAsync(input, context, TestContext.Current.CancellationToken);
        result.IsError.ShouldBe(!consent);
        server.LogEntries.Count().ShouldBe(consent ? 1 : 0);
        if (consent)
        {
            result.Text.ShouldContain("report-1");
            server.LogEntries.Single().RequestMessage.ShouldNotBeNull().Headers.ShouldNotBeNull().ContainsKey("Idempotency-Key").ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Should_RejectProtectedAndOutsideAttachments_When_PreparingPublication()
    {
        var publisher = Substitute.For<IBriefPublisher>();
        publisher.Destination.Returns(new Uri("https://reports.example/briefs"));
        var tool = new BriefPublishTool(publisher, Substitute.For<IUserInteraction>(), new LocalFileSystem());
        var context = new ToolContext { Workspace = Path.GetTempPath(), ProtectedGlobs = [".env*"] };
        foreach (var path in new[] { "../secret", ".env.local" })
            await Should.ThrowAsync<ToolArgumentException>(() => tool.ExecuteAsync(ToolArguments.Parse(System.Text.Json.JsonSerializer.Serialize(new { title = "Report", text = "Summary", attachments = new[] { path } })), context, TestContext.Current.CancellationToken));
    }
}