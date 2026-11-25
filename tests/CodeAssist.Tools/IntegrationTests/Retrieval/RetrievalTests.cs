using CodeAssist.Tools;
using CodeAssist.Workspaces;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class RetrievalTests
{
    [Fact]
    public async Task Should_RankCurrentChunksWithoutProtectedSource_When_Retrieving()
    {
        var root = Path.Combine(Path.GetTempPath(), "ca-retrieval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(root, "cache.cs"), "Expire stored entries after their deadline.", token);
            await File.WriteAllTextAsync(Path.Combine(root, ".env.local"), "private credential", token);
            var context = new ToolContext { Workspace = root, ProtectedGlobs = [".env*"] };
            var vectors = Substitute.For<ITextEmbeddings>();
            vectors.Name.Returns("fixture-semantic");
            vectors.GenerateAsync(Arg.Any<IReadOnlyList<string>>(), token).Returns(call =>
            {
                var texts = call.Arg<IReadOnlyList<string>>();
                texts.Count.ShouldBe(2);
                texts.ShouldNotContain(text => text.Contains("private credential", StringComparison.Ordinal));
                return (IReadOnlyList<float[]>)texts.Select(_ => new float[] { 1, 0 }).ToArray();
            });
            var service = new WorkspaceRetrieval(vectors);
            var lexical = await service.SearchAsync("Expire", "lexical", ".", 5, context, token);
            lexical.Matches.Single().StartLine.ShouldBe(1);
            (await service.SearchAsync("Expire", "lexical", "cache.cs", 5, context, token)).Matches.Single().Path.ShouldBe("cache.cs");
            await Should.ThrowAsync<IOException>(() => service.SearchAsync("Expire", "lexical", "missing", 5, context, token));
            var semantic = await service.SearchAsync("time-based invalidation", "semantic", ".", 5, context, token);
            semantic.Matches.Single().Path.ShouldBe("cache.cs");
            semantic.Matches.Single().Score.ShouldBe(1);
            await File.WriteAllTextAsync(Path.Combine(root, "cache.cs"), "Changed", token);
            (await service.SearchAsync("Expire", "lexical", ".", 5, context, token)).Matches.ShouldBeEmpty();
            await Should.ThrowAsync<InvalidOperationException>(() => new WorkspaceRetrieval().SearchAsync("query", "semantic", ".", 5, context, token));
            var interaction = Substitute.For<IUserInteraction>();
            vectors.Destination.Returns("https://embeddings.example.invalid/");
            var denied = await new WorkspaceSearchTool(service, interaction).ExecuteAsync(ToolArguments.Parse("""{"query":"search","strategy":"semantic"}"""), context, token);
            denied.IsError.ShouldBeTrue();
            vectors.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "GenerateAsync").ShouldBe(1);
            vectors.GenerateAsync(Arg.Any<IReadOnlyList<string>>(), token).Returns(async call =>
            {
                await File.WriteAllTextAsync(Path.Combine(root, "cache.cs"), "Changed while embedding", token);
                return (IReadOnlyList<float[]>)call.Arg<IReadOnlyList<string>>().Select(_ => new float[] { 1, 0 }).ToArray();
            });
            var stale = await service.SearchAsync("query", "semantic", ".", 5, context, token);
            stale.Matches.ShouldBeEmpty();
            stale.ScanComplete.ShouldBeFalse();
        }
        finally { Directory.Delete(root, true); }
    }
}