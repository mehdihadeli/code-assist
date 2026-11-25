using CodeAssist.Models;
using CodeAssist.Extensions.Skills;
using System.Net;
using System.Text;
using CodeAssist.Server.Composition;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Configuration;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class ToolExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ca-tools-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly LocalFileSystem _fs = new();

    public ToolExecutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ToolContext Context(ToolLimits? limits = null) => new()
    {
        Workspace = _root,
        Limits = limits ?? new ToolLimits(),
        ProtectedGlobs = [".env*"],
        IgnoreGlobs = ["node_modules/**"],
    };

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private Task<ToolResult> Run(ITool tool, string json, ToolContext? context = null) =>
        tool.ExecuteAsync(ToolArguments.Parse(json), context ?? Context(), TestContext.Current.CancellationToken);

    private ValueTask<ToolPlan> Plan(ITool tool, string json) =>
        tool.PlanAsync(ToolArguments.Parse(json), Context(), TestContext.Current.CancellationToken);

    // fs.read

    [Fact]
    public async Task Should_RecheckProtectionForNormalizedNames_When_ExactFilenameIsMissing()
    {
        Write("caf\u00e9.txt", "normalized file");
        var tool = new FsReadTool(_fs);
        var input = ToolArguments.Parse("{\"path\":\"caf\\u0065\\u0301.txt\"}");
        var token = TestContext.Current.CancellationToken;
        var plan = await tool.PlanAsync(input, Context(), token);
        plan.Effects.First().Target.ShouldBe("caf\u00e9.txt");
        (await tool.ExecuteAsync(input, Context(), token)).Text.ShouldContain("normalized file");
        var protectedContext = new ToolContext { Workspace = _root, ProtectedGlobs = ["caf\u00e9.txt"] };
        (await tool.PlanAsync(input, protectedContext, token)).Effects.First().Protected.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_PageOversizedTextAndDeclareGuidanceReads_When_ReadingNestedFiles()
    {
        Write("AGENTS.md", "Keep changes focused.");
        Write("nested/AGENTS.md", "Use local conventions.");
        Write("nested/large.txt", string.Join('\n', Enumerable.Range(1, 2000).Select(number => "line " + number)));
        var tool = new FsReadTool(_fs);
        var context = Context(new ToolLimits { MaxFileBytes = 32 });
        var input = ToolArguments.Parse("""{"path":"nested/large.txt","offset":1500,"limit":2}""");
        var plan = await tool.PlanAsync(input, context, TestContext.Current.CancellationToken);
        plan.Effects.ShouldContain(effect => effect.Target == "AGENTS.md");
        plan.Effects.ShouldContain(effect => effect.Target == "nested/AGENTS.md");
        var result = await tool.ExecuteAsync(input, context, TestContext.Current.CancellationToken);
        result.IsError.ShouldBeFalse(result.Text);
        result.Text.ShouldContain("1500: line 1500");
        result.Text.ShouldContain("Use local conventions.");
        result.Text.ShouldContain("never a permission grant");
        var blocked = new ToolContext { Workspace = _root, ProtectedGlobs = ["**/AGENTS.md", "AGENTS.md"] };
        (await tool.ExecuteAsync(input, blocked, TestContext.Current.CancellationToken)).Text.ShouldNotContain("Use local conventions.");
    }

    [Fact]
    public async Task Should_ReadOnlyBoundedBundleResources_When_LoadingSkills()
    {
        Write("skills/review/SKILL.md", "---\nname: review\ndescription: Review changes.\n---\nCheck code.");
        Write("skills/review/examples/check.txt", "Example content");
        Write("skills/review/.env.local", "not-for-models");
        var skills = new CodeAssist.Extensions.Skills.LocalSkillCatalog([Path.Combine(_root, "skills")]);
        var tool = new SkillLoadTool(skills);
        var loaded = await Run(tool, """{"id":"review","input":"changed files"}""");
        loaded.Text.ShouldContain("examples/check.txt");
        loaded.Text.ShouldContain("changed files");
        (await Run(tool, """{"id":"review","path":"examples/check.txt"}""")).Text.ShouldBe("Example content");
        (await Run(tool, """{"id":"review","path":"../../escape"}""")).IsError.ShouldBeTrue();
        (await Run(tool, """{"id":"review","path":".env.local"}""")).IsError.ShouldBeTrue();
        (await Run(tool, """{"id":"review","path":"examples/check.txt"}""", Context(new ToolLimits { MaxFileBytes = 2 }))).IsError.ShouldBeTrue();
        (await Plan(tool, """{"id":"review"}""")).Effects.Single().Kind.ShouldBe(ToolEffectKind.Read);
    }

    [Fact]
    public async Task Should_RejectOversizedTextAndCancellation_When_ReadingFiles()
    {
        Write("large.txt", new string('x', 8192));
        var tool = new FsReadTool(_fs);
        var result = await Run(tool, """{"path":"large.txt"}""", Context(new ToolLimits { MaxFileBytes = 1024 }));
        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("byte text-file limit");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            ToolArguments.Parse("""{"path":"large.txt"}"""), Context(), cancellation.Token));
    }

    [Fact]
    public async Task ReadReturnsNumberedLinesAndContinuation()
    {
        Write("a.txt", string.Join('\n', Enumerable.Range(1, 30).Select(i => $"row{i}")));

        var result = await Run(new FsReadTool(_fs), """{"path":"a.txt","offset":5,"limit":3}""");

        result.IsError.ShouldBeFalse();
        result.Text.ShouldBe("5: row5\n6: row6\n7: row7\n[more available: continue with offset=8]");
    }

    [Fact]
    public async Task ReadListsDirectoriesAndRejectsBinary()
    {
        Write("src/a.cs", "x");
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [1, 0, 2, 0]);

        var listing = await Run(new FsReadTool(_fs), """{"path":"src"}""");
        var binary = await Run(new FsReadTool(_fs), """{"path":"bin.dat"}""");

        listing.Text.ShouldBe("a.cs\nsub/");
        binary.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAttachesImages()
    {
        File.WriteAllBytes(Path.Combine(_root, "p.png"), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);

        var result = await Run(new FsReadTool(_fs), """{"path":"p.png"}""");

        result.Attachments.ShouldNotBeNull().Single().MimeType.ShouldBe("image/png");
    }

    [Fact]
    public async Task ReadPlanFlagsOutsideAndProtectedPaths()
    {
        var outside = await Plan(new FsReadTool(_fs), """{"path":"../elsewhere.txt"}""");
        var secret = await Plan(new FsReadTool(_fs), """{"path":".env.local"}""");

        outside.Effects.Single().Outside.ShouldBeTrue();
        secret.Effects.Single().Protected.ShouldBeTrue();
        secret.Effects.Single().Target.ShouldBe(".env.local");
    }

    [Fact]
    public async Task ReadRejectsInvalidArguments()
    {
        await Should.ThrowAsync<ToolArgumentException>(() => Run(new FsReadTool(_fs), """{"offset":0}"""));
    }

    [Fact]
    public async Task DiffComparesProposalsAndFilesWithoutWriting()
    {
        var original = Write("before.txt", "old\n");
        Write("after.txt", "new\n");
        var tool = new FsDiffTool(_fs);
        var proposal = await Run(tool, """{"path":"before.txt","content":"new\n"}""");
        var comparison = await Run(tool, """{"path":"before.txt","otherPath":"after.txt"}""");
        proposal.IsError.ShouldBeFalse();
        proposal.Text.ShouldContain("-old");
        proposal.Text.ShouldContain("+new");
        comparison.Text.ShouldBe(proposal.Text);
        File.ReadAllText(original).ShouldBe("old\n");
        (await Run(tool, """{"path":"before.txt","content":"old\n"}""")).Text.ShouldBe("No differences.");
        var plan = await Plan(tool, """{"path":"before.txt","otherPath":"../after.txt"}""");
        plan.Effects.Count.ShouldBe(2);
        plan.Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
        plan.Effects[1].Outside.ShouldBeTrue();
    }

    [Fact]
    public async Task DiffRejectsInvalidInputsAndBoundsResults()
    {
        Write("before.txt", "old");
        var tool = new FsDiffTool(_fs);
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool, """{"path":"before.txt"}"""));
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool, """{"path":"before.txt","content":"","otherPath":"other.txt"}"""));
        (await Run(tool, """{"path":"missing","content":""}""")).IsError.ShouldBeTrue();
        (await Run(tool, """{"path":"before.txt","otherPath":"missing"}""")).IsError.ShouldBeTrue();
        (await Run(tool, """{"path":"before.txt","content":"too large"}""", Context(new ToolLimits { MaxFileBytes = 4 }))).IsError.ShouldBeTrue();
        File.WriteAllBytes(Path.Combine(_root, "binary"), [0, 1, 0]);
        (await Run(tool, """{"path":"before.txt","otherPath":"binary"}""")).IsError.ShouldBeTrue();
        var bounded = await Run(tool, System.Text.Json.JsonSerializer.Serialize(new { path = "before.txt", content = new string('x', 500) }),
            Context(new ToolLimits { OutputMaxBytes = 80 }));
        bounded.Text.ShouldContain("truncated");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            ToolArguments.Parse("""{"path":"before.txt","content":"new"}"""), Context(), cancelled.Token));
    }

    // fs.find

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Should_ReturnHashAndRequestedDiffContext_When_ProposingChanges(int contextLines)
    {
        var path = Write("context.txt", "before\nold\nafter\n");
        var arguments = System.Text.Json.JsonSerializer.Serialize(new { path = "context.txt", content = "before\nnew\nafter\n", contextLines });

        var result = await Run(new FsDiffTool(_fs), arguments);

        result.IsError.ShouldBeFalse(result.Text);
        result.Text.ShouldContain(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        result.Text.ShouldContain("-old");
        result.Text.ShouldContain("+new");
        result.Text.Contains(" before", StringComparison.Ordinal).ShouldBe(contextLines == 1);
        File.ReadAllText(path).ShouldBe("before\nold\nafter\n");
    }

    [Fact]
    public async Task Should_ShowBlankLineAndFinalNewlineChanges_When_ComparingDetailedDiffs()
    {
        Write("endings.txt", "value\n");

        var blank = await Run(new FsDiffTool(_fs), """{"path":"endings.txt","content":"value\n\n"}""");
        var missing = await Run(new FsDiffTool(_fs), """{"path":"endings.txt","content":"value"}""");

        blank.IsError.ShouldBeFalse();
        blank.Text.ShouldContain("@@");
        blank.Text.ShouldNotBe("No differences.");
        missing.Text.ShouldContain("\\ No newline at end of file");
        missing.Text.ShouldContain("-value");
        missing.Text.ShouldContain("+value");
    }

    [Fact]
    public async Task Should_RejectInvalidDiffContext_When_RequestedOutsideLimits()
    {
        Write("context.txt", "old");

        await Should.ThrowAsync<ToolArgumentException>(() => Run(new FsDiffTool(_fs),
            """{"path":"context.txt","content":"new","contextLines":21}"""));
    }

    [Fact]
    public async Task FindListsByGlobSkippingIgnoredAndHidden()
    {
        Write("src/a.cs", "x");
        Write("src/deep/b.cs", "x");
        Write("src/c.txt", "x");
        Write("node_modules/p/i.cs", "x");
        Write(".hidden/h.cs", "x");

        var result = await Run(new FsFindTool(_fs), """{"glob":"**/*.cs"}""");

        result.Text.ShouldBe("src/a.cs\nsrc/deep/b.cs");
    }

    [Fact]
    public async Task FindSearchesContentWithLineNumbers()
    {
        Write("a.cs", "one\nneedle here\nthree\n");
        Write("b.cs", "no match\n");
        File.WriteAllBytes(Path.Combine(_root, "c.bin"), [0, 1, 2, 3]);

        var literal = await Run(new FsFindTool(_fs), """{"text":"NEEDLE"}""");
        var regex = await Run(new FsFindTool(_fs), """{"text":"^thr.e$","regex":true}""");
        var none = await Run(new FsFindTool(_fs), """{"text":"absent"}""");

        literal.Text.ShouldStartWith("a.cs:2: needle here");
        literal.Text.ShouldContain("binary or oversized files skipped");
        regex.Text.ShouldStartWith("a.cs:3: three");
        none.Text.ShouldStartWith("No matches.");
    }

    [Fact]
    public async Task FindReportsLimitsAndBadRegex()
    {
        Write("a.txt", string.Join('\n', Enumerable.Repeat("hit", 10)));

        var limited = await Run(new FsFindTool(_fs), """{"text":"hit","limit":3}""");

        limited.Text.Split('\n').Count(l => l.StartsWith("a.txt:")).ShouldBe(3);
        limited.Text.ShouldContain("limit of 3 reached");
        await Should.ThrowAsync<ToolArgumentException>(() => Run(new FsFindTool(_fs), """{"text":"(","regex":true}"""));
    }

    [Fact]
    public async Task FindStopsReadingOnceItsMatchBudgetIsFilled()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hit\n", 100_000)));
        using var stream = new SearchReadStream(bytes);
        var files = Substitute.For<IFileSystem>();
        files.Stat(Arg.Any<string>()).Returns(new FileFacts(bytes.Length, false, DateTimeOffset.UtcNow));
        files.OpenRead(Arg.Any<string>()).Returns(stream);

        var result = await Run(new FsFindTool(files), """{"path":"large.txt","text":"hit","limit":2}""");

        result.Text.ShouldContain("limit of 2 reached");
        stream.BytesRead.ShouldBeLessThan(20_000);
    }

    [Fact]
    public async Task FindReportsItsDeadlineWithoutSwallowingCallerCancellation()
    {
        var bytes = Encoding.UTF8.GetBytes("hit\n");
        using var stream = new SearchReadStream(bytes, block: true);
        var files = Substitute.For<IFileSystem>();
        files.Stat(Arg.Any<string>()).Returns(new FileFacts(bytes.Length, false, DateTimeOffset.UtcNow));
        files.OpenRead(Arg.Any<string>()).Returns(stream);

        var result = await Run(new FsFindTool(files), """{"path":"slow.txt","text":"hit"}""",
            Context(new ToolLimits { SearchTimeout = TimeSpan.FromMilliseconds(50) }));

        result.Text.ShouldContain("search stopped");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => new FsFindTool(_fs).ExecuteAsync(
            ToolArguments.Parse("""{"text":"hit"}"""), Context(), cancelled.Token));
    }

    private sealed class SearchReadStream(byte[] bytes, bool block = false) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (block) await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }

    // fs.write / fs.edit

    [Fact]
    public async Task WriteCreatesParentsAndKeepsBom()
    {
        var created = await Run(new FsWriteTool(_fs), """{"path":"x/y/new.txt","content":"hello\n"}""");
        File.ReadAllText(Path.Combine(_root, "x", "y", "new.txt")).ShouldBe("hello\n");
        created.Text.ShouldStartWith("Created x/y/new.txt");

        var withBom = Path.Combine(_root, "bom.txt");
        File.WriteAllBytes(withBom, [0xEF, 0xBB, 0xBF, (byte)'a']);
        await Run(new FsWriteTool(_fs), """{"path":"bom.txt","content":"b"}""");
        File.ReadAllBytes(withBom).ShouldBe([0xEF, 0xBB, 0xBF, (byte)'b']);
    }

    [Fact]
    public async Task WritePlanShowsADiffAndHashGuardBlocksStaleWrites()
    {
        var path = Write("f.txt", "old\n");

        var plan = await Plan(new FsWriteTool(_fs), """{"path":"f.txt","content":"new\n"}""");
        var stale = await Run(new FsWriteTool(_fs), """{"path":"f.txt","content":"new\n","expectHash":"deadbeef"}""");

        plan.Preview.ShouldNotBeNull().ShouldContain("-old");
        plan.Effects.Single().Kind.ShouldBe(ToolEffectKind.Write);
        stale.IsError.ShouldBeTrue();
        stale.Text.ShouldContain("changed since it was read");
        File.ReadAllText(path).ShouldBe("old\n");
    }

    [Fact]
    public async Task EditReplacesOnceAndExplainsFailures()
    {
        var path = Write("e.txt", "alpha\nbeta\nbeta\n");

        var ambiguous = await Run(new FsEditTool(_fs), """{"path":"e.txt","oldString":"beta","newString":"B"}""");
        var missing = await Run(new FsEditTool(_fs), """{"path":"e.txt","oldString":"gamma","newString":"G"}""");
        var ok = await Run(new FsEditTool(_fs), """{"path":"e.txt","oldString":"alpha","newString":"A"}""");

        ambiguous.Text.ShouldContain("matches 2 places");
        missing.Text.ShouldContain("not found");
        ok.IsError.ShouldBeFalse();
        File.ReadAllText(path).ShouldBe("A\nbeta\nbeta\n");
        await Should.ThrowAsync<ToolArgumentException>(() => Run(new FsEditTool(_fs), """{"path":"e.txt","oldString":"x","newString":"x"}"""));
    }

    [Fact]
    public async Task EditHashPreconditionUsesTheHashFromAnEarlierResult()
    {
        var path = Write("h.txt", "one\n");
        var first = await Run(new FsEditTool(_fs), """{"path":"h.txt","oldString":"one","newString":"two"}""");
        var hash = first.Text[(first.Text.LastIndexOf("sha256: ", StringComparison.Ordinal) + 8)..].Trim();

        var ok = await Run(new FsEditTool(_fs), $$"""{"path":"h.txt","oldString":"two","newString":"three","expectHash":"{{hash}}"}""");
        File.WriteAllText(path, "changed elsewhere\n");
        var stale = await Run(new FsEditTool(_fs), $$"""{"path":"h.txt","oldString":"changed","newString":"x","expectHash":"{{hash}}"}""");

        ok.IsError.ShouldBeFalse();
        stale.IsError.ShouldBeTrue();
    }

    // fs.patch

    [Fact]
    public async Task Should_PreserveNotebookMetadataAndClearOutputs_When_CodeCellIsReplaced()
    {
        var path = Write("sample.ipynb", """
            {"nbformat":4,"nbformat_minor":5,"metadata":{"custom":"keep"},"cells":[
              {"id":"stable","cell_type":"code","metadata":{"tag":"keep"},"source":["old"],"execution_count":7,"outputs":[{"output_type":"stream","text":"stale"}]}]}
            """);

        var result = await Run(new NotebookEditTool(_fs), """{"path":"sample.ipynb","operation":"replace","index":0,"source":"print(42)"}""");

        result.IsError.ShouldBeFalse(result.Text);
        result.Text.ShouldContain("sha256:");
        var notebook = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        notebook["metadata"]!["custom"]!.GetValue<string>().ShouldBe("keep");
        var cell = notebook["cells"]![0]!;
        cell["id"]!.GetValue<string>().ShouldBe("stable");
        cell["metadata"]!["tag"]!.GetValue<string>().ShouldBe("keep");
        cell["source"]!.GetValue<string>().ShouldBe("print(42)");
        cell["outputs"]!.AsArray().ShouldBeEmpty();
        cell["execution_count"].ShouldBeNull();
    }

    [Fact]
    public async Task Should_InsertAndRemoveNotebookCells_When_IndicesAreValid()
    {
        var path = Write("cells.ipynb", """{"nbformat":4,"nbformat_minor":5,"metadata":{},"cells":[]}""");
        var tool = new NotebookEditTool(_fs);

        var inserted = await Run(tool, """{"path":"cells.ipynb","operation":"insert","index":0,"source":"# Title","kind":"markdown"}""");

        inserted.IsError.ShouldBeFalse(inserted.Text);
        var cell = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["cells"]![0]!;
        cell["id"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        cell["cell_type"]!.GetValue<string>().ShouldBe("markdown");
        (await Run(tool, """{"path":"cells.ipynb","operation":"remove","index":0}""")).IsError.ShouldBeFalse();
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["cells"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_LeaveNotebookUnchanged_When_PreviewedOrHashIsStale()
    {
        var path = Write("guard.ipynb", """{"nbformat":4,"metadata":{},"cells":[]}""");
        var original = File.ReadAllBytes(path);
        var tool = new NotebookEditTool(_fs);
        var preview = """{"path":"guard.ipynb","operation":"insert","index":0,"source":"x","dryRun":true}""";

        (await Run(tool, preview)).IsError.ShouldBeFalse();
        (await Plan(tool, preview)).Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
        (await Run(tool, """{"path":"guard.ipynb","operation":"insert","index":0,"source":"x","expectHash":"stale"}""")).IsError.ShouldBeTrue();

        File.ReadAllBytes(path).ShouldBe(original);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"nbformat\":3,\"cells\":[]}")]
    [InlineData("{\"nbformat\":4,\"cells\":[]}")]
    public async Task Should_RejectNotebookChangeWithoutWriting_When_DocumentOrIndexIsInvalid(string contents)
    {
        var path = Write("invalid.ipynb", contents);

        var result = await Run(new NotebookEditTool(_fs), """{"path":"invalid.ipynb","operation":"replace","index":0,"source":"x"}""");

        result.IsError.ShouldBeTrue();
        File.ReadAllText(path).ShouldBe(contents);
    }

    [Fact]
    public async Task PatchAppliesAllOperations()
    {
        var update = Write("u.txt", "a\nb\n");
        Write("gone.txt", "bye");
        Write("old/name.txt", "moved");

        var result = await Run(new FsPatchTool(_fs), """
            {"operations":[
              {"op":"add","path":"new/n.txt","content":"fresh"},
              {"op":"update","path":"u.txt","edits":[{"oldString":"b","newString":"B"}]},
              {"op":"delete","path":"gone.txt"},
              {"op":"move","path":"old/name.txt","to":"new/name.txt"}]}
            """);

        result.IsError.ShouldBeFalse(result.Text);
        File.ReadAllText(Path.Combine(_root, "new", "n.txt")).ShouldBe("fresh");
        File.ReadAllText(update).ShouldBe("a\nB\n");
        File.Exists(Path.Combine(_root, "gone.txt")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_root, "new", "name.txt")).ShouldBe("moved");
        File.Exists(Path.Combine(_root, "old", "name.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task PatchChangesNothingWhenAnyOperationFails()
    {
        var first = Write("p1.txt", "keep");

        var result = await Run(new FsPatchTool(_fs), """
            {"operations":[
              {"op":"update","path":"p1.txt","edits":[{"oldString":"keep","newString":"changed"}]},
              {"op":"update","path":"p1-missing.txt","edits":[{"oldString":"x","newString":"y"}]}]}
            """);

        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("Operation 2");
        result.Text.ShouldContain("No files were changed");
        File.ReadAllText(first).ShouldBe("keep");
    }

    [Fact]
    public async Task PatchRollsBackWhenAWriteFailsMidway()
    {
        var first = Write("r1.txt", "one");
        Write("blocked", "a regular file where a directory is needed");

        var result = await Run(new FsPatchTool(_fs), """
            {"operations":[
              {"op":"update","path":"r1.txt","edits":[{"oldString":"one","newString":"ONE"}]},
              {"op":"add","path":"blocked/inner.txt","content":"x"}]}
            """);

        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("rolled back");
        File.ReadAllText(first).ShouldBe("one");
    }

    [Fact]
    public async Task Should_PreviewDetailedPatchWithoutWrites_When_DryRunIsRequested()
    {
        var path = Write("preview.txt", "before\n");
        var arguments = """
            {"dryRun":true,"operations":[
              {"op":"update","path":"preview.txt","edits":[{"oldString":"before","newString":"after"}]},
              {"op":"add","path":"not-created.txt","content":"new\n"}]}
            """;
        var tool = new FsPatchTool(_fs);

        var plan = await Plan(tool, arguments);
        var result = await Run(tool, arguments);

        result.IsError.ShouldBeFalse(result.Text);
        result.Text.ShouldContain("-before");
        result.Text.ShouldContain("+after");
        plan.Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
        File.ReadAllText(path).ShouldBe("before\n");
        File.Exists(Path.Combine(_root, "not-created.txt")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("move")]
    public async Task Should_RejectEntirePatch_When_SourceHashIsStale(string operation)
    {
        var path = Write("guarded.txt", "original\n");
        var arguments = System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = new object[]
            {
                new { op = "add", path = "must-not-exist.txt", content = "new" },
                new { op = operation, path = "guarded.txt", to = "moved.txt", expectHash = "stale", edits = new[] { new { oldString = "original", newString = "changed" } } },
            },
        });

        var result = await Run(new FsPatchTool(_fs), arguments);

        result.IsError.ShouldBeTrue();
        File.ReadAllText(path).ShouldBe("original\n");
        File.Exists(Path.Combine(_root, "must-not-exist.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "moved.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_ReturnDetailedChanges_When_PatchIsApplied()
    {
        Write("detail.txt", "old\n");
        Write("delete-detail.txt", "removed\n");

        var result = await Run(new FsPatchTool(_fs), """
            {"operations":[
              {"op":"update","path":"detail.txt","edits":[{"oldString":"old","newString":"new"}]},
              {"op":"delete","path":"delete-detail.txt"}]}
            """);

        result.IsError.ShouldBeFalse(result.Text);
        result.Text.ShouldContain("-old");
        result.Text.ShouldContain("+new");
        result.Text.ShouldContain("-removed");
        result.Text.ShouldContain("+++ /dev/null");
    }

    [Fact]
    public async Task Should_RollBackCompletedChanges_When_PatchIsCancelledBetweenWrites()
    {
        var first = Write("cancel-first.txt", "one");
        var second = Write("cancel-second.txt", "two");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var files = Substitute.For<IFileSystem>();
        files.Stat(Arg.Any<string>()).Returns(call => _fs.Stat(call.Arg<string>()));
        files.OpenRead(Arg.Any<string>()).Returns(call => _fs.OpenRead(call.Arg<string>()));
        files.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _fs.ReadAllBytesAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        files.WriteAtomicAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                await _fs.WriteAtomicAsync(call.Arg<string>(), call.Arg<ReadOnlyMemory<byte>>(), token);
                if (token.CanBeCanceled) cancellation.Cancel();
            });
        var arguments = ToolArguments.Parse("""
            {"operations":[
              {"op":"update","path":"cancel-first.txt","edits":[{"oldString":"one","newString":"ONE"}]},
              {"op":"update","path":"cancel-second.txt","edits":[{"oldString":"two","newString":"TWO"}]}]}
            """);

        await Should.ThrowAsync<OperationCanceledException>(() => new FsPatchTool(files).ExecuteAsync(arguments, Context(), cancellation.Token));

        File.ReadAllText(first).ShouldBe("one");
        File.ReadAllText(second).ShouldBe("two");
    }

    [Fact]
    public async Task PatchRejectsDuplicatePathsAndGivesEffectsAndPreview()
    {
        Write("d.txt", "x");

        await Should.ThrowAsync<ToolArgumentException>(() => Run(new FsPatchTool(_fs),
            """{"operations":[{"op":"delete","path":"d.txt"},{"op":"delete","path":"d.txt"}]}"""));

        var plan = await Plan(new FsPatchTool(_fs), """
            {"operations":[{"op":"delete","path":"d.txt"},{"op":"add","path":"n.txt","content":"hi"}]}
            """);
        plan.Effects.Select(e => e.Kind).ShouldBe([ToolEffectKind.Delete, ToolEffectKind.Write]);
        plan.Preview.ShouldNotBeNull().ShouldContain("delete d.txt");
    }

    // proc.run

    [Fact]
    public async Task ProcRunReturnsOutputAndExitCode()
    {
        var tool = new ProcRunTool(new ProcessRunner());

        var ok = await Run(tool, """{"command":"echo hello-tool"}""");
        var failed = await Run(tool, """{"command":"exit 3"}""");

        ok.IsError.ShouldBeFalse(ok.Text);
        ok.Text.ShouldStartWith("Exit code 0");
        ok.Text.ShouldContain("hello-tool");
        failed.IsError.ShouldBeTrue();
        failed.Text.ShouldStartWith("Exit code 3");
    }

    [Fact]
    public async Task ProcRunStopsLongProcessesAtTheTimeout()
    {
        var tool = new ProcRunTool(new ProcessRunner());
        var command = OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 30" : "sleep 30";
        var shell = ShellLocator.Locate();
        if (shell.Name == "cmd") return;

        var started = DateTime.UtcNow;
        var result = await Run(tool, $$"""{"command":"{{command}}","timeoutSeconds":1}""");

        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("Timed out");
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ProcRunPlanHasOneEffectPerCommand()
    {
        var plan = await Plan(new ProcRunTool(new ProcessRunner()), """{"command":"git status && rm -rf build; ls"}""");

        plan.Effects.Select(e => e.Target).ShouldBe(["git status", "rm -rf build", "ls"]);
        plan.Effects.ShouldAllBe(e => e.Kind == ToolEffectKind.Exec);
    }

    // web.fetch

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Html(string body, string type = "text/html") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Fact]
    public async Task FetchConvertsHtmlToMarkdown()
    {
        var tool = new WebFetchTool(new HttpClient(new StubHandler(_ => Html("<h1>Hi</h1><p>there</p>"))));

        var result = await Run(tool, """{"url":"https://example.com/page"}""");

        result.Text.ShouldContain("# Hi");
        result.Text.ShouldContain("there");
    }

    [Fact]
    public async Task FetchBlocksLocalTargetsAndPrivateRedirects()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "example.com"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://169.254.169.254/latest") } }
            : Html("secret"));
        var tool = new WebFetchTool(new HttpClient(handler));

        var local = await Run(tool, """{"url":"http://localhost:8080/x"}""");
        var literal = await Run(tool, """{"url":"http://10.0.0.5/x"}""");
        var redirected = await Run(tool, """{"url":"https://example.com/r"}""");

        local.IsError.ShouldBeTrue();
        literal.IsError.ShouldBeTrue();
        redirected.IsError.ShouldBeTrue();
        redirected.Text.ShouldContain("not a public address");
        handler.Requests.Select(u => u.Host).ShouldBe(["example.com"]);
    }

    [Fact]
    public async Task FetchRejectsBinaryOversizedAndBadSchemes()
    {
        var tool = new WebFetchTool(new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath == "/big"
            ? Html(new string('x', 2000), "text/plain")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { { "Content-Type", "image/png" } } } })));

        var image = await Run(tool, """{"url":"https://example.com/a.png"}""");
        var big = await Run(tool, """{"url":"https://example.com/big"}""", Context(new ToolLimits { FetchMaxBytes = 1000 }));
        var scheme = await Run(tool, """{"url":"file:///etc/passwd"}""");

        image.Text.ShouldContain("image/png");
        big.Text.ShouldContain("larger than");
        scheme.IsError.ShouldBeTrue();
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool, """{"url":"not a url"}"""));
    }

    [Fact]
    public async Task FetchClipsLongPages()
    {
        var page = string.Join('\n', Enumerable.Range(1, 300).Select(i => $"line {i}"));
        var tool = new WebFetchTool(new HttpClient(new StubHandler(_ => Html(page, "text/plain"))));

        var result = await Run(tool, """{"url":"https://example.com/long"}""", Context(new ToolLimits { OutputMaxLines = 50 }));

        result.Text.ShouldContain("truncated: 250 more lines omitted");
    }

    // interaction tools

    private sealed class FakeInteraction : IUserInteraction
    {
        public IReadOnlyList<QuestionAnswer>? Answers { get; init; }
        public IReadOnlyList<Question>? Asked { get; private set; }

        public Task<IReadOnlyList<QuestionAnswer>?> AskAsync(IReadOnlyList<Question> questions, ToolContext context, CancellationToken cancellationToken)
        {
            Asked = questions;
            return Task.FromResult(Answers);
        }
    }

    [Fact]
    public async Task AskUserRendersAnswersAndTreatsDismissalAsNeutral()
    {
        const string json = """{"questions":[{"text":"Which API?","options":[{"label":"Breaking","recommended":true},{"label":"Compat"}]}]}""";
        var answered = new FakeInteraction { Answers = [new QuestionAnswer(["Compat"], "but keep v1")] };

        var result = await Run(new AskUserTool(answered), json);
        var dismissed = await Run(new AskUserTool(new FakeInteraction()), json);

        result.Text.ShouldBe("Which API? -> Compat; but keep v1");
        answered.Asked!.Single().Options[0].Recommended.ShouldBeTrue();
        dismissed.IsError.ShouldBeFalse();
        dismissed.Text.ShouldContain("dismissed");
    }

    private sealed class FakeTodos : ITodoStore
    {
        public IReadOnlyList<TodoItem>? Items { get; private set; }

        public Task ReplaceAsync(Guid sessionId, IReadOnlyList<TodoItem> items, CancellationToken cancellationToken)
        {
            Items = items;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task PlanTodoStoresItemsAndEnforcesOneInProgress()
    {
        var store = new FakeTodos();
        var tool = new PlanTodoTool(store);

        var result = await Run(tool, """{"items":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"}]}""");

        result.Text.ShouldContain("1 completed, 1 in progress");
        store.Items!.Select(i => i.Status).ShouldBe([TodoStatus.Completed, TodoStatus.InProgress]);
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool,
            """{"items":[{"content":"a","status":"in_progress"},{"content":"b","status":"in_progress"}]}"""));
    }

    private sealed class FakeSkills : ISkillCatalog
    {
        public IReadOnlyList<SkillSummary> Index { get; } = [new("deploy", "Ship it")];

        public Task<SkillContent?> LoadAsync(string id, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult<SkillContent?>(id == "deploy" ? new SkillContent("deploy", "Run the pipeline.", ["run.sh"]) : null);
    }

    [Fact]
    public async Task Should_LoadBodyOrListAvailableSkills_When_SkillToolIsInvoked()
    {
        var tool = new SkillLoadTool(new FakeSkills());

        var loaded = await Run(tool, """{"id":"deploy"}""");
        var missing = await Run(tool, """{"id":"nope"}""");

        loaded.Text.ShouldContain("Run the pipeline.");
        loaded.Text.ShouldContain("run.sh");
        missing.IsError.ShouldBeTrue();
        missing.Text.ShouldContain("deploy");
        tool.Spec.Description.ShouldContain("deploy");
        tool.Spec.Description.ShouldContain("Ship it");
    }

    private sealed class FakeDelegation : IDelegation
    {
        public IReadOnlyList<string> Roles { get; } = ["reviewer"];
        public DelegationRequest? Last { get; private set; }

        public Task<DelegationResult> RunAsync(DelegationRequest request, ToolContext context, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new DelegationResult(Guid.Empty, "looks fine", false));
        }
    }

    [Fact]
    public async Task DelegateChecksTheRoleAndPassesTheRequest()
    {
        var fake = new FakeDelegation();
        var tool = new AgentDelegateTool(fake);

        var bad = await Run(tool, """{"role":"builder","description":"d","prompt":"p"}""");
        var good = await Run(tool, """{"role":"reviewer","description":"review","prompt":"check the diff"}""");

        bad.IsError.ShouldBeTrue();
        good.Text.ShouldStartWith("looks fine");
        fake.Last!.Prompt.ShouldBe("check the diff");
    }

    [Fact]
    public async Task ResourceToolsDiscoverTemplatesAndReadCustomUris()
    {
        var source = Substitute.For<IResourceSource>();
        source.Servers.Returns(new[] { "records" });
        source.ListAsync("records", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(
            new ResourceCatalog([new("record:42", "Answer")], [new("record:{id}", "Record lookup")]));
        source.ReadAsync("records", "record:42", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ResourceContent>?>([new("record:42", "the answer")]));

        var list = await Run(new ResourceListTool(source), "{}");
        var read = await Run(new ResourceReadTool(source), """{"server":"records","uri":"record:42"}""");
        var plan = await Plan(new ResourceReadTool(source), """{"server":"records","uri":"record:42"}""");

        list.Text.ShouldContain("record:{id}");
        list.Text.ShouldContain("record:42");
        read.Text.ShouldContain("the answer");
        plan.Effects.Select(effect => effect.Target).ShouldBe(["resource:records", "resource:records:record:42"]);
        var catalog = ToolComposition.CreateCatalog(new ToolSettings(), new ToolPorts(Resources: source));
        catalog.Find("resource.list").ShouldNotBeNull();
        catalog.Find("resource.read").ShouldNotBeNull();
        ToolComposition.CreateCatalog(new ToolSettings()).Find("resource.read").ShouldBeNull();
    }

    [Fact]
    public async Task ResourceToolsBoundMediaAndTextAndValidateArguments()
    {
        var source = Substitute.For<IResourceSource>();
        source.Servers.Returns(new[] { "records" });
        byte[] image = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        source.ReadAsync("records", "record:42", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ResourceContent>?>([new("record:42", new string('x', 500), image), new("record:43", Data: image)]));
        var tool = new ResourceReadTool(source);

        var result = await Run(tool, """{"server":"records","uri":"record:42"}""", Context(new ToolLimits { OutputMaxBytes = 100 }));
        var oversized = await Run(tool, """{"server":"records","uri":"record:42"}""", Context(new ToolLimits { MaxAttachmentBytes = 10 }));

        result.Text.ShouldContain("truncated");
        result.Attachments!.Count.ShouldBe(2);
        result.Attachments[0].MimeType.ShouldBe("image/png");
        oversized.IsError.ShouldBeTrue();
        oversized.Attachments.ShouldBeNull();
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool, """{"server":"missing","uri":"record:42"}"""));
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool, """{"server":"records","uri":"relative/path"}"""));
    }

    [Fact]
    public async Task ResourceToolsHandleMissingFailuresAndCancellation()
    {
        var source = Substitute.For<IResourceSource>();
        source.Servers.Returns(new[] { "records" });
        source.ReadAsync("records", "record:missing", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ResourceContent>?>(null));
        var tool = new ResourceReadTool(source);
        var missing = await Run(tool, """{"server":"records","uri":"record:missing"}""");
        missing.IsError.ShouldBeTrue();
        source.ReadAsync("records", "record:fail", Arg.Any<ToolContext>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<IReadOnlyList<ResourceContent>?>(new IOException("connection closed")));
        var failed = await Run(tool, """{"server":"records","uri":"record:fail"}""");
        failed.Text.ShouldContain("connection closed");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            ToolArguments.Parse("""{"server":"records","uri":"record:42"}"""), Context(), cancelled.Token));
    }

    [Fact]
    public async Task JobsDetachFromTurnCancellationAndPublishBoundedResults()
    {
        await using var jobs = new InMemoryToolJobs();
        using var turn = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Completed += job => finished.TrySetResult(job);
        var context = Context(new ToolLimits { OutputMaxBytes = 20 });
        var started = jobs.Start("independent work", async token =>
        {
            await release.Task.WaitAsync(token);
            return ToolResult.Ok(new string('x', 500));
        }, context, turn.Token);
        turn.Cancel();
        jobs.Read(Guid.NewGuid(), started.Id).ShouldBeNull();
        jobs.List(Guid.NewGuid()).ShouldBeEmpty();
        release.SetResult();
        var completed = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        completed.Status.ShouldBe(ToolJobStatus.Completed);
        completed.Result!.Text.ShouldContain("truncated");
        var read = await Run(new JobTool(jobs, JobOperation.Read), System.Text.Json.JsonSerializer.Serialize(new { id = started.Id }));
        read.Text.ShouldContain("Completed");
    }

    [Fact]
    public async Task JobsStopOnlyOwnedWorkAndEvictOnlyFinishedEntries()
    {
        await using var jobs = new InMemoryToolJobs(capacity: 1);
        var started = jobs.Start("wait", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ToolResult.Ok("unexpected");
        }, Context(), TestContext.Current.CancellationToken);
        (await jobs.StopAsync(Guid.NewGuid(), started.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => jobs.Start("over capacity", _ => Task.FromResult(ToolResult.Ok("x")), Context(), TestContext.Current.CancellationToken));
        var stopped = await jobs.StopAsync(Context().SessionId, started.Id, TestContext.Current.CancellationToken);
        stopped!.Status.ShouldBe(ToolJobStatus.Cancelled);
        var next = jobs.Start("next", _ => Task.FromResult(ToolResult.Ok("ok")), Context(), TestContext.Current.CancellationToken);
        jobs.Read(Context().SessionId, started.Id).ShouldBeNull();
        jobs.Read(Context().SessionId, next.Id).ShouldNotBeNull();
        var plan = await Plan(new JobTool(jobs, JobOperation.Stop), System.Text.Json.JsonSerializer.Serialize(new { id = next.Id }));
        plan.Effects.Single().Kind.ShouldBe(ToolEffectKind.Exec);
    }

    [Fact]
    public async Task JobsEnforceLifetimeAndIsolateFailingNotificationSubscribers()
    {
        await using var jobs = new InMemoryToolJobs(maximumDuration: TimeSpan.FromMilliseconds(50));
        var finished = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Completed += _ => throw new InvalidOperationException("broken observer");
        jobs.Completed += job => finished.TrySetResult(job);
        jobs.Start("deadline", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ToolResult.Ok("unexpected");
        }, Context(), TestContext.Current.CancellationToken);
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        result.Status.ShouldBe(ToolJobStatus.TimedOut);
    }

    [Fact]
    public async Task JobsBackCommandsAndDelegationWithoutAdvertisingMissingPorts()
    {
        await using var jobs = new InMemoryToolJobs();
        var completed = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Completed += job => completed.TrySetResult(job);
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(new ProcessOutcome(0, "background output", 0, false, false));
        var launched = await Run(new ProcRunTool(runner, jobs), """{"command":"echo test","background":true}""");
        launched.Text.ShouldContain("Background job:");
        (await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Result!.Text.ShouldContain("background output");

        var delegated = new TaskCompletionSource<ToolJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Completed += job => delegated.TrySetResult(job);
        var result = await Run(new AgentDelegateTool(new FakeDelegation(), jobs), """{"role":"reviewer","description":"review code","prompt":"check it","background":true}""");
        result.Text.ShouldContain("Background delegation job:");
        (await delegated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Result!.Text.ShouldContain("child session:");
        var unavailable = await Run(new ProcRunTool(runner), """{"command":"echo test","background":true}""");
        unavailable.IsError.ShouldBeTrue();
        var catalog = ToolComposition.CreateCatalog(new ToolSettings(), new ToolPorts(Jobs: jobs));
        catalog.Find("job.stop").ShouldNotBeNull();
        ToolComposition.CreateCatalog(new ToolSettings()).Find("job.stop").ShouldBeNull();
    }

    // catalog

    [Fact]
    public async Task ModelFindFiltersPagesAndPreservesUnknownMetadata()
    {
        var catalog = Substitute.For<CodeAssist.Models.IModelCatalog>();
        catalog.ListProviders().Returns([new CodeAssist.Contracts.CatalogProviderDto("test", "Test Provider", null, [], null, null)]);
        catalog.List().Returns([
            new CodeAssist.Contracts.CatalogModelDto("test", "alpha", "Alpha", null, null, false, false, ["text"], ["text"], null, null),
            new CodeAssist.Contracts.CatalogModelDto("test", "vision", "Vision", 1000, 100, true, true, ["text", "image"], ["text"], null, null),
        ]);
        var tool = new ModelFindTool(catalog);
        var first = await Run(tool, """{"provider":"Test Provider","limit":1}""");
        using var document = System.Text.Json.JsonDocument.Parse(first.Text);
        document.RootElement.GetProperty("total").GetInt32().ShouldBe(2);
        document.RootElement.GetProperty("nextOffset").GetInt32().ShouldBe(1);
        document.RootElement.GetProperty("models")[0].GetProperty("tools").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await Run(tool, """{"capability":"vision","query":"test vision"}""")).Text.ShouldContain("test/vision");
        (await Run(tool, """{"offset":2}""")).Text.ShouldContain("\"models\":[]");
        (await Plan(tool, "{}")).Effects.ShouldAllBe(effect => effect.Kind == ToolEffectKind.Read);
        (await Plan(tool, """{"refresh":true}""")).Effects.ShouldContain(effect => effect.Kind == ToolEffectKind.Net);
        ToolComposition.CreateCatalog(new ToolSettings(), new ToolPorts(Models: catalog)).Find("model.find").ShouldNotBeNull();
    }

    [Fact]
    public async Task ModelFindValidatesRefreshFailuresAndCancellation()
    {
        var catalog = Substitute.For<CodeAssist.Models.IModelCatalog>();
        catalog.RefreshAsync(Arg.Any<CancellationToken>()).Returns(new CodeAssist.Contracts.CatalogRefreshDto("failed", 0, "offline"));
        var tool = new ModelFindTool(catalog);
        (await Run(tool, """{"refresh":true}""")).IsError.ShouldBeTrue();
        await Should.ThrowAsync<ToolArgumentException>(() => Run(tool, """{"limit":0,"capability":"invented"}"""));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => tool.ExecuteAsync(ToolArguments.Parse("{}"), Context(), cancelled.Token));
        await catalog.DidNotReceive().RefreshAsync(cancelled.Token);
    }

    [Fact]
    public void CatalogRegistersOnlyToolsThatCanWorkAndHonoursDisabled()
    {
        var settings = new ToolSettings { Disabled = ["proc.run"] };

        var plain = ToolComposition.CreateCatalog(settings);
        var withPorts = ToolComposition.CreateCatalog(new ToolSettings(), new ToolPorts(new FakeInteraction(), new FakeTodos()));

        plain.All.Select(t => t.Spec.Name).ShouldBe(["fs.read", "fs.find", "fs.diff", "fs.write", "fs.edit", "fs.patch", "notebook.edit", "web.fetch"]);
        withPorts.Find("ask.user").ShouldNotBeNull();
        withPorts.Find("plan.todo").ShouldNotBeNull();
        withPorts.Find("agent.delegate").ShouldBeNull();
    }

    [Fact]
    public void RolesNarrowTheCatalogByNameOrTagAndDenyWins()
    {
        var catalog = ToolComposition.CreateCatalog(new ToolSettings(), new ToolPorts(new FakeInteraction(), new FakeTodos()));

        catalog.Visible(["read"]).Select(t => t.Spec.Name).ShouldBe(["fs.read", "fs.find", "fs.diff", "ask.user", "plan.todo"]);
        var withoutExecAndNet = catalog.Visible(null, ["exec", "net"]).Select(t => t.Spec.Name).ToList();
        withoutExecAndNet.ShouldNotContain("proc.run");
        withoutExecAndNet.ShouldNotContain("web.fetch");
        catalog.Visible(["write", "fs.read"], ["fs.patch"]).Select(t => t.Spec.Name).ShouldBe(["fs.read", "fs.write", "fs.edit", "notebook.edit"]);
        catalog.Visible().Count.ShouldBe(catalog.All.Count);
    }

    [Fact]
    public async Task Should_DiscoverOnlyVisibleTools_When_RoleAndSettingsHideTools()
    {
        var settings = new CodeAssistSettings();
        settings.Tools.Disabled = ["fs.write"];
        settings.Roles[settings.Agent.Role].Tools.Deny = ["fs.read"];
        var catalog = ToolComposition.CreateCatalog(settings);
        var search = catalog.Find("tool.find").ShouldNotBeNull();

        var result = await Run(search, """{"query":"fs.","includeSchemas":true}""");

        result.IsError.ShouldBeFalse(result.Text);
        using var json = System.Text.Json.JsonDocument.Parse(result.Text);
        var tools = json.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        var names = tools.Select(tool => tool.GetProperty("Name").GetString()).ToArray();
        names.ShouldContain("fs.diff");
        names.ShouldNotContain("fs.read");
        names.ShouldNotContain("fs.write");
        tools.ShouldAllBe(tool => tool.GetProperty("schema").ValueKind == System.Text.Json.JsonValueKind.Object);
        var unknown = new ToolContext { Workspace = _root, Role = "unknown-role" };
        var hidden = await Run(search, "{}", unknown);
        using var hiddenJson = System.Text.Json.JsonDocument.Parse(hidden.Text);
        hiddenJson.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Should_PageToolDiscoveryWithoutSchemas_When_Requested()
    {
        var search = ToolComposition.CreateCatalog(new CodeAssistSettings()).Find("tool.find").ShouldNotBeNull();

        var result = await Run(search, """{"limit":1}""");

        result.IsError.ShouldBeFalse(result.Text);
        using var json = System.Text.Json.JsonDocument.Parse(result.Text);
        json.RootElement.GetProperty("nextOffset").GetInt32().ShouldBe(1);
        var tools = json.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        tools.ShouldHaveSingleItem();
        tools[0].GetProperty("schema").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public void ToolNamesAreUniqueAndSchemasAreValidJson()
    {
        var catalog = ToolComposition.CreateCatalog(new ToolSettings(),
            new ToolPorts(new FakeInteraction(), new FakeTodos(), new FakeSkills(), new FakeDelegation()));

        foreach (var tool in catalog.All)
        {
            System.Text.Json.JsonDocument.Parse(tool.Spec.InputSchema).Dispose();
            tool.Spec.Description.ShouldNotBeNullOrWhiteSpace();
        }
        Should.Throw<InvalidOperationException>(() => new ToolCatalog([new FsReadTool(_fs), new FsReadTool(_fs)]));
    }

    [Fact]
    public void SettingsMapToLimitsAndContext()
    {
        var settings = new CodeAssistSettings();
        settings.Tools.OutputMaxLines = 500;
        settings.Tools.Web.FetchTimeoutSeconds = 45;

        var context = ToolComposition.CreateContext(settings, _root, Guid.NewGuid(), "call-1");

        context.Limits.OutputMaxLines.ShouldBe(500);
        context.Limits.FetchDefaultSeconds.ShouldBe(45);
        context.ProtectedGlobs.ShouldContain(".git/**");
        context.IgnoreGlobs.ShouldContain("node_modules/**");
    }
}
