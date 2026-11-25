using CodeAssist.Tools.Text;
using Shouldly;

namespace CodeAssist.Sessions.Domain.UnitTests.Text;

public sealed class TextEditTests
{
    [Fact]
    public void ReplacesASingleExactMatch()
    {
        var outcome = TextEdit.Replace("a\nb\nc\n", "b", "B");

        outcome.Ok.ShouldBeTrue();
        outcome.Content.ShouldBe("a\nB\nc\n");
    }

    [Fact]
    public void RejectsAmbiguousMatchesUnlessReplaceAll()
    {
        TextEdit.Replace("x x", "x", "y").Failure.ShouldBe(EditFailure.Ambiguous);
        TextEdit.Replace("x x", "x", "y", replaceAll: true).Content.ShouldBe("y y");
    }

    [Fact]
    public void ReportsMissingAndEmptyOldText()
    {
        TextEdit.Replace("abc", "zzz", "y").Failure.ShouldBe(EditFailure.NotFound);
        TextEdit.Replace("abc", "", "y").Failure.ShouldBe(EditFailure.EmptyOldText);
    }

    [Fact]
    public void MultiLineTextWithLfMatchesCrlfFiles()
    {
        var outcome = TextEdit.Replace("one\r\ntwo\r\nthree\r\n", "one\ntwo", "1\n2");

        outcome.Content.ShouldBe("1\r\n2\r\nthree\r\n");
    }

    [Fact]
    public void TolerantModeIgnoresQuotesDashesAndTrailingSpaces()
    {
        var content = "say \u201Chi\u201D \u2013 now   \nnext\n";

        TextEdit.Replace(content, "say \"hi\" - now", "bye").Failure.ShouldBe(EditFailure.NotFound);
        var outcome = TextEdit.Replace(content, "say \"hi\" - now", "bye", tolerant: true);

        outcome.Ok.ShouldBeTrue();
        outcome.Content.ShouldBe("bye\nnext\n");
    }
}

