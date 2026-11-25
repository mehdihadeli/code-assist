using CodeAssist.Tools.Text;
using Shouldly;

namespace CodeAssist.Sessions.Domain.UnitTests.Text;

public sealed class TextHelpersTests
{
    [Fact]
    public void UnifiedDiffShowsOnlyChangedHunksWithContext()
    {
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}"));
        var after = before.Replace("line10", "LINE10");

        var diff = UnifiedDiff.Create("f.txt", before, after);

        diff.ShouldContain("--- a/f.txt");
        diff.ShouldContain("-line10");
        diff.ShouldContain("+LINE10");
        diff.ShouldContain(" line9");
        diff.ShouldNotContain("line1\n");
        UnifiedDiff.Count(before, after).ShouldBe(new DiffSummary(1, 1));
    }

    [Fact]
    public void UnifiedDiffForNewFileUsesDevNull()
    {
        UnifiedDiff.Create("new.txt", null, "a\nb").ShouldContain("--- /dev/null");
        UnifiedDiff.Create("same.txt", "a", "a").ShouldBe("");
    }

    [Fact]
    public void LineWindowPagesAndNamesTheNextOffset()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"l{i}");

        var page = LineWindow.Slice(lines, offset: 3, limit: 4, maxLineChars: 100, maxBytes: 10_000);

        page.Text.ShouldBe("3: l3\n4: l4\n5: l5\n6: l6");
        page.NextOffset.ShouldBe(7);
        LineWindow.Slice(lines, 9, 4, 100, 10_000).NextOffset.ShouldBeNull();
    }

    [Fact]
    public void LineWindowShortensLongLines()
    {
        var page = LineWindow.Slice([new string('x', 50)], 1, 10, maxLineChars: 10, maxBytes: 10_000);

        page.Text.ShouldContain("(+40 chars)");
    }

    [Fact]
    public void OutputBudgetKeepsHeadOrTail()
    {
        var text = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"row{i}"));

        var head = OutputBudget.Head(text, maxLines: 10, maxBytes: 10_000);
        head.Truncated.ShouldBeTrue();
        head.Text.ShouldEndWith("row10\n");
        head.OmittedLines.ShouldBe(90);

        var tail = OutputBudget.Tail(text, maxChars: 30);
        tail.Text.ShouldEndWith("row100");
        tail.OmittedChars.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void OutputBudgetClipsFirstLineWithoutSplittingUtf8Characters()
    {
        var text = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 10));
        var result = OutputBudget.Head(text, maxLines: 10, maxBytes: 9);

        result.Truncated.ShouldBeTrue();
        System.Text.Encoding.UTF8.GetByteCount(result.Text).ShouldBe(8);
        result.OmittedChars.ShouldBe(16);
        OutputBudget.Head("abc", 0, 100).Text.ShouldBeEmpty();
        OutputBudget.Head("abc", 10, 0).Text.ShouldBeEmpty();
    }

    [Fact]
    public void ContentSnifferRecognizesMediaAndBinary()
    {
        ContentSniffer.AttachmentMime([0x89, 0x50, 0x4E, 0x47, 0x0D]).ShouldBe("image/png");
        ContentSniffer.AttachmentMime("%PDF-1.7"u8).ShouldBe("application/pdf");
        ContentSniffer.AttachmentMime("hello"u8).ShouldBeNull();
        ContentSniffer.IsBinary([1, 2, 0, 3]).ShouldBeTrue();
        ContentSniffer.IsBinary("text"u8).ShouldBeFalse();
    }

    [Theory]
    [InlineData("git status", "git status")]
    [InlineData("a && b || c; d | e", "a|b|c|d|e")]
    [InlineData("echo \"a && b\" && ls", "echo \"a && b\"|ls")]
    [InlineData("cmd 2>&1 | tail", "cmd 2>&1|tail")]
    public void CommandSplitterSeparatesCommandsOutsideQuotes(string line, string expected)
    {
        var split = CommandSplitter.Split(line);

        string.Join('|', split.Segments).ShouldBe(expected);
        split.Complex.ShouldBeFalse();
    }

    [Fact]
    public void CommandSplitterReturnsSubstitutionLinesWhole()
    {
        var split = CommandSplitter.Split("echo $(rm -rf x) && ls");

        split.Complex.ShouldBeTrue();
        split.Segments.ShouldBe(["echo $(rm -rf x) && ls"]);
    }

    [Fact]
    public void HtmlBecomesMarkdown()
    {
        var markdown = HtmlText.ToMarkdown("<html><head><title>x</title></head><body><h1>Title</h1><p>Hello <a href=\"https://e.com\">link</a> &amp; <code>code</code></p><ul><li>one</li></ul><script>bad()</script></body></html>");

        markdown.ShouldContain("# Title");
        markdown.ShouldContain("[link](https://e.com)");
        markdown.ShouldContain("& `code`");
        markdown.ShouldContain("- one");
        markdown.ShouldNotContain("bad()");
    }

}

