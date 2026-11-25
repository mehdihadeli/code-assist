using CodeAssist.Tools;
using CodeAssist.Tools.Extensions;
using CodeAssist.Workspaces.Platform;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class TestFeedbackTests
{
    [Theory]
    [InlineData("dtd")]
    [InlineData("cancelled")]
    [InlineData("empty")]
    public async Task Should_NeverReportCleanSuccess_When_ReportIsUnsafeCancelledOrEmpty(string condition)
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-trx-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = TestContext.Current.CancellationToken;
            var runner = Substitute.For<IProcessRunner>();
            runner.RunAsync(Arg.Any<ProcessRequest>(), token).Returns(async call =>
            {
                var args = call.Arg<ProcessRequest>().Arguments;
                var folder = args[args.ToList().IndexOf("--results-directory") + 1];
                await File.WriteAllTextAsync(Path.Combine(folder, "report.trx"), condition == "dtd" ? "<!DOCTYPE TestRun [<!ENTITY leak SYSTEM 'file:///unavailable'>]><TestRun>&leak;</TestRun>" : "<TestRun/>", token);
                return new ProcessOutcome(0, "", 0, false, condition == "cancelled");
            });
            var service = new TrxTestFeedback(root, runner, [new("fixture", "fixture", [], true)]);
            var context = new ToolContext { Workspace = Path.GetTempPath(), SessionId = Guid.NewGuid() };
            if (condition == "dtd") await Should.ThrowAsync<System.Xml.XmlException>(() => service.RunAsync("fixture", null, context, token));
            else (await service.RunAsync("fixture", null, context, token)).Status.ShouldBe(condition);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_PreserveFailuresAndOwnership_When_RunningTrustedProfiles(bool writeReport)
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-test-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var runner = Substitute.For<IProcessRunner>();
            runner.RunAsync(Arg.Any<ProcessRequest>(), token).Returns(async call =>
            {
                var request = call.Arg<ProcessRequest>();
                request.FilePath.ShouldBe("fixture-runner");
                request.Arguments.ShouldContain("--report-trx");
                var folder = request.Arguments[request.Arguments.ToList().IndexOf("--results-directory") + 1];
                if (writeReport) await File.WriteAllTextAsync(Path.Combine(folder, "report.trx"), """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="1" testName="Fails" outcome="Failed"><Output><ErrorInfo><Message>expected 2</Message><StackTrace>at Fixture.Test()</StackTrace></ErrorInfo></Output></UnitTestResult></Results></TestRun>""", token);
                return new ProcessOutcome(1, "fixture failed", 0, false, false);
            });
            var profiles = new[] { new TestExecutionProfile("unit", "fixture-runner", [], true, "--filter-class") };
            var service = new TrxTestFeedback(Path.Combine(root, "reports"), runner, profiles);
            var context = new ToolContext { Workspace = root, SessionId = Guid.NewGuid() };
            var answer = await new TestFeedbackTool(service).ExecuteAsync(ToolArguments.Parse("""{"operation":"run","profile":"unit","filter":"*Tests"}"""), context, token);
            answer.IsError.ShouldBeTrue();
            var payload = answer.StructuredContent.ShouldNotBeNull();
            payload.GetProperty("Status").GetString().ShouldBe(writeReport ? "failed" : "report-unavailable");
            var id = payload.GetProperty("Id").GetGuid();
            var reopened = new TrxTestFeedback(Path.Combine(root, "reports"), runner, profiles);
            var saved = await reopened.ReadAsync(id, context, token);
            if (writeReport) saved.Cases.Single().Message.ShouldBe("expected 2");
            await Should.ThrowAsync<IOException>(() => reopened.ReadAsync(id, new ToolContext { Workspace = root, SessionId = Guid.NewGuid() }, token));
            await File.WriteAllTextAsync(Path.Combine(root, "WidgetTests.cs"), "fixture", token);
            (await service.RelatedAsync("Widget.cs", context, token)).ShouldContain("WidgetTests.cs");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}