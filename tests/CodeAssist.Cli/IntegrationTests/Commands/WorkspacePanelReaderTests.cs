using System.Text;
using CodeAssist.Cli.Commands.Conversation;
using CodeAssist.Testing;
using CodeAssist.Tui.FullScreen;
using Shouldly;

namespace CodeAssist.Cli.IntegrationTests.Commands;

public sealed class WorkspacePanelReaderTests : IDisposable
{
    private readonly SettingsSandbox files = new();
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;
    public void Dispose() => files.Dispose();

    [Fact]
    public async Task Should_DenyAllWorkspaceReads_When_ConsentIsAbsent()
    {
        var reader = new WorkspacePanelReader(Path.Combine(files.Root, "missing"), [], false);
        Should.Throw<UnauthorizedAccessException>(() => reader.ReadFiles(Cancellation));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => reader.ReadPreviewAsync("missing.txt", Cancellation));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => reader.ReadBranchAsync(Cancellation));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => reader.ReadChangesAsync(Cancellation));
    }

    [Fact]
    public async Task Should_ListOnlyAllowedSortedEntries_When_DefaultAndCustomExclusionsApply()
    {
        foreach (var name in new[] { "z.txt", "a.md", ".env", ".env.local", "secret.key", "cert.pem", "cert.pfx", "private/data.txt", "nested/.env.prod", "nested/secret.txt", "nested/file.cs", "bin/a.dll", "obj/x", "node_modules/a.js", ".code-assist/state", ".git/config" }) Write(name, "safe");
        var reader = new WorkspacePanelReader(files.Work, ["private/**", "**/secret.txt"], true);
        reader.ReadFiles(Cancellation).Select(entry => entry.Path).ShouldBe(["a.md", "nested/", "nested/file.cs", "z.txt"]);
        (await reader.ReadPreviewAsync("nested/file.cs", Cancellation)).ShouldBe(["safe"]);
        foreach (var name in new[] { ".env", ".env.local", "secret.key", "cert.pem", "cert.pfx", "private/data.txt", "nested/.env.prod", "nested/secret.txt", "bin/a.dll", ".git/config" })
            await Should.ThrowAsync<UnauthorizedAccessException>(() => reader.ReadPreviewAsync(name, Cancellation));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("absolute")]
    [InlineData("prefix-sibling")]
    [InlineData("alternate-stream")]
    [InlineData("control")]
    public async Task Should_RejectEscapingOrHostilePaths_When_PreviewIsRequested(string partition)
    {
        var outside = Path.Combine(files.Root, "outside.txt");
        File.WriteAllText(outside, "outside");
        var path = partition switch
        {
            "parent" => "../../outside.txt",
            "absolute" => outside,
            "prefix-sibling" => Path.Combine(files.Work + "-other", "file.txt"),
            "alternate-stream" => "file.txt:secret",
            _ => "file\u001b.txt"
        };
        var reader = new WorkspacePanelReader(files.Work, [], true);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => reader.ReadPreviewAsync(path, Cancellation));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("root")]
    public async Task Should_RejectLinkedPaths_When_EntryOrAncestorHasReparseAttributes(string partition)
    {
        var link = Path.Combine(files.Work, "linked");
        if (partition == "file") File.WriteAllText(link, "protected");
        else { Directory.CreateDirectory(link); File.WriteAllText(Path.Combine(link, "safe.txt"), "protected"); }
        var reader = new WorkspacePanelReader(partition == "root" ? link : files.Work, [], true,
            path => File.GetAttributes(path) | (path == link ? FileAttributes.ReparsePoint : 0));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => reader.ReadPreviewAsync(partition == "file" ? "linked" : partition == "root" ? "safe.txt" : "linked/safe.txt", Cancellation));
        if (partition != "root") reader.ReadFiles(Cancellation).ShouldBeEmpty();
        else Should.Throw<UnauthorizedAccessException>(() => reader.ReadFiles(Cancellation));
    }

    [Theory]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    public async Task Should_EnforceExactPreviewByteLimit_When_SizeCrosses64KiB(int size)
    {
        Write("sized.txt", new string('x', size));
        var reader = new WorkspacePanelReader(files.Work, [], true);
        if (size > 65536) await Should.ThrowAsync<IOException>(() => reader.ReadPreviewAsync("sized.txt", Cancellation));
        else (await reader.ReadPreviewAsync("sized.txt", Cancellation)).Single().Length.ShouldBe(size);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("invalid-utf8")]
    public async Task Should_RejectNonTextPreview_When_BytesAreUnsafe(string partition)
    {
        File.WriteAllBytes(Path.Combine(files.Work, "bytes.txt"), partition == "binary" ? [65, 0, 66] : [0xff, 0xfe, 65]);
        var reader = new WorkspacePanelReader(files.Work, [], true);
        await Should.ThrowAsync<IOException>(() => reader.ReadPreviewAsync("bytes.txt", Cancellation));
    }

    [Fact]
    public async Task Should_PreserveLiteralTextAndBoundLines_When_Utf8PreviewContainsMarkupAndBom()
    {
        var content = "\ufeff[bold]literal[/]\r\n" + string.Join('\n', Enumerable.Range(1, 130).Select(index => "line " + index));
        Write("literal.txt", content);
        var reader = new WorkspacePanelReader(files.Work, [], true);
        var lines = await reader.ReadPreviewAsync("literal.txt", Cancellation);
        lines.Count.ShouldBe(120);
        lines[0].ShouldBe("[bold]literal[/]");
        lines[^1].ShouldBe("line 119");
    }

    [Fact]
    public async Task Should_ObserveCancellation_When_FileReadsStartCanceled()
    {
        Write("safe.txt", "safe");
        using var source = new CancellationTokenSource();
        source.Cancel();
        var reader = new WorkspacePanelReader(files.Work, [], true);
        Should.Throw<OperationCanceledException>(() => reader.ReadFiles(source.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadPreviewAsync("safe.txt", source.Token));
    }

    [Fact]
    public void Should_BoundInventoryDepthAndCount_When_WorkspaceExceedsLimits()
    {
        Write("one/two/three/four/five/six/allowed.txt", "allowed");
        Write("one/two/three/four/five/six/seven/hidden.txt", "hidden");
        var reader = new WorkspacePanelReader(files.Work, [], true);
        var entries = reader.ReadFiles(Cancellation);
        entries.ShouldContain(entry => entry.Path == "one/two/three/four/five/six/allowed.txt");
        entries.ShouldNotContain(entry => entry.Path.EndsWith("hidden.txt", StringComparison.Ordinal));
        for (var index = 0; index < 260; index++) Write("item-" + index.ToString("D3") + ".txt", "safe");
        var bounded = reader.ReadFiles(Cancellation);
        bounded.Count.ShouldBe(256);
        bounded.Select(entry => entry.Path).ShouldBe(bounded.Select(entry => entry.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Should_ParseAllChangeGroupsAndRenameDestinations_When_PorcelainSnapshotIsMixed()
    {
        var entries = WorkspacePanelReader.ParseChanges("M  staged.cs\0 M unstaged.cs\0MM both.cs\0?? literal space.txt\0UU conflict.cs\0AA added.cs\0DD deleted.cs\0R  renamed.cs\0old.cs\0 M following.cs\0");
        entries.ShouldBe(new WorkspaceFileEntry[]
        {
            new("staged.cs", "M") { Group = "Staged" }, new("unstaged.cs", "M") { Group = "Unstaged" },
            new("both.cs", "M") { Group = "Unstaged" }, new("both.cs", "M") { Group = "Staged" },
            new("literal space.txt", "?") { Group = "Untracked" }, new("conflict.cs", "!") { Group = "Conflicts" },
            new("added.cs", "!") { Group = "Conflicts" }, new("deleted.cs", "!") { Group = "Conflicts" },
            new("renamed.cs", "R") { Group = "Staged" }, new("following.cs", "M") { Group = "Unstaged" }
        });
    }

    [Theory]
    [InlineData("rename-unstaged")]
    [InlineData("copy-staged")]
    [InlineData("rename-and-edit")]
    public void Should_PreserveDestinationStatus_When_RenameOrCopyHasSourceField(string partition)
    {
        var status = partition switch { "rename-unstaged" => " R", "copy-staged" => "C ", _ => "RM" };
        var entries = WorkspacePanelReader.ParseChanges(status + " destination.cs\0source.cs\0");
        entries.ShouldBe(partition switch
        {
            "rename-unstaged" => new WorkspaceFileEntry[] { new("destination.cs", "R") { Group = "Unstaged" } },
            "copy-staged" => [new("destination.cs", "C") { Group = "Staged" }],
            _ => [new("destination.cs", "M") { Group = "Unstaged" }, new("destination.cs", "R") { Group = "Staged" }]
        });
    }

    [Theory]
    [InlineData("short")]
    [InlineData("separator")]
    [InlineData("missing-source")]
    [InlineData("control-destination")]
    [InlineData("control-source")]
    [InlineData("unterminated")]
    [InlineData("empty-interior")]
    public void Should_RejectMalformedOrHostileSnapshot_When_StatusRecordsAreUnsafe(string partition)
    {
        var output = partition switch
        {
            "short" => "M \0", "separator" => "MM:file.cs\0", "missing-source" => "R  renamed.cs\0",
            "control-destination" => "?? evil\u001b[31m.cs\0", "control-source" => "R  renamed.cs\0old\nname.cs\0",
            "unterminated" => " M file.cs", _ => " M first.cs\0\0 M lost.cs\0"
        };
        Should.Throw<IOException>(() => WorkspacePanelReader.ParseChanges(output));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    public void Should_EnforceExactGitEntryLimit_When_InventoryCrosses256(int count)
    {
        var output = string.Concat(Enumerable.Range(0, count).Select(index => "?? file-" + index + ".cs\0"));
        if (count > 256) Should.Throw<IOException>(() => WorkspacePanelReader.ParseChanges(output));
        else WorkspacePanelReader.ParseChanges(output).Count.ShouldBe(count);
        WorkspacePanelReader.ParseChanges("").ShouldBeEmpty();
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(files.Work, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }
}