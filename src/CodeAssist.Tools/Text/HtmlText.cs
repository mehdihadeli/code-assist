using System.Net;
using System.Text.RegularExpressions;

namespace CodeAssist.Tools.Text;

/// <summary>A small, dependency-free HTML to Markdown/plain-text conversion for fetched pages. Not a full renderer.</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<!--.*?-->|<(script|style|noscript|svg|head|template)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Dropped();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Link();

    [GeneratedRegex(@"<pre\b[^>]*>(.*?)</pre\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Pre();

    [GeneratedRegex(@"<code\b[^>]*>(.*?)</code\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Code();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<p\b[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ParagraphStart();

    [GeneratedRegex(@"</(p|div|tr|ul|ol|table|section|article|header|footer|blockquote)\s*>|<br\s*/?>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Break();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, 2000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]+\n", RegexOptions.None, 2000)]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.None, 2000)]
    private static partial Regex BlankRuns();

    public static bool LooksLikeHtml(string text) =>
        text.AsSpan(0, Math.Min(text.Length, 1024)).Contains("<html", StringComparison.OrdinalIgnoreCase) ||
        text.AsSpan(0, Math.Min(text.Length, 1024)).Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);

    public static string ToMarkdown(string html)
    {
        var text = Dropped().Replace(html, "");
        text = Pre().Replace(text, m => "\n\n```\n" + Tag().Replace(m.Groups[1].Value, "") + "\n```\n\n");
        text = Heading().Replace(text, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " " + Tag().Replace(m.Groups[2].Value, "").Trim() + "\n\n");
        text = Link().Replace(text, m => $"[{Tag().Replace(m.Groups[2].Value, "").Trim()}]({m.Groups[1].Value})");
        text = Code().Replace(text, m => "`" + Tag().Replace(m.Groups[1].Value, "") + "`");
        text = ListItem().Replace(text, "\n- ");
        text = ParagraphStart().Replace(text, "\n\n");
        return Finish(text);
    }

    public static string ToText(string html)
    {
        var text = Dropped().Replace(html, "");
        text = ListItem().Replace(text, "\n- ");
        text = ParagraphStart().Replace(text, "\n\n");
        return Finish(text);
    }

    private static string Finish(string text)
    {
        text = Break().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace("\r", "");
        text = TrailingSpace().Replace(text, "\n");
        return BlankRuns().Replace(text, "\n\n").Trim();
    }
}
