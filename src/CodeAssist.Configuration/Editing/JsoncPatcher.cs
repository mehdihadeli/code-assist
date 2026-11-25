using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAssist.Configuration;

/// <summary>
/// Edits a JSON-with-comments settings file in place: comments, key order and formatting around the touched key are preserved.
/// Only object paths are supported; lists are replaced as a whole.
/// </summary>
public static class JsoncPatcher
{
    private sealed record Member(string Key, int KeyStart, int ValueStart, int ValueEnd, int Comma);

    private sealed record ObjectInfo(int Open, int Close, List<Member> Members);

    public static string Set(string text, IReadOnlyList<string> path, JsonNode value)
    {
        if (path.Count == 0) throw new ArgumentException("Path is empty.", nameof(path));
        if (string.IsNullOrWhiteSpace(text)) text = "{}" + Environment.NewLine;

        var root = SkipTrivia(text, 0);
        if (root >= text.Length || text[root] != '{') throw new FormatException("The settings file must contain a JSON object.");

        var result = SetIn(text, root, path, 0, value, Eol(text));
        Verify(result);
        return result;
    }

    public static string Remove(string text, IReadOnlyList<string> path)
    {
        var current = text;
        var remaining = path.ToList();
        while (remaining.Count > 0)
        {
            var located = Locate(current, remaining);
            if (located is null) break;
            current = RemoveMember(current, located.Value.Parent, located.Value.Member);
            remaining.RemoveAt(remaining.Count - 1);

            // Prune parent objects that became empty.
            if (remaining.Count == 0) break;
            var parent = Locate(current, remaining);
            if (parent is null || current[parent.Value.Member.ValueStart] != '{' || ParseObject(current, parent.Value.Member.ValueStart).Members.Count > 0) break;
        }
        Verify(current);
        return current;
    }

    private static string SetIn(string text, int objectStart, IReadOnlyList<string> path, int depth, JsonNode value, string eol)
    {
        var info = ParseObject(text, objectStart);
        var segment = path[depth];
        var last = depth == path.Count - 1;
        var member = info.Members.FirstOrDefault(m => string.Equals(m.Key, segment, StringComparison.OrdinalIgnoreCase));

        if (member is not null)
        {
            var memberIndent = LineIndent(text, member.KeyStart);
            if (last) return Splice(text, member.ValueStart, member.ValueEnd, Render(value, memberIndent, eol));
            if (text[member.ValueStart] == '{') return SetIn(text, member.ValueStart, path, depth + 1, value, eol);
            return Splice(text, member.ValueStart, member.ValueEnd, Render(Nest(path, depth + 1, value), memberIndent, eol));
        }

        var node = last ? value : Nest(path, depth + 1, value);
        return Insert(text, info, segment, node, eol);
    }

    private static string Insert(string text, ObjectInfo info, string key, JsonNode node, string eol)
    {
        var objectIndent = LineIndent(text, info.Open);
        var childIndent = info.Members.Count > 0 && OnlyWhitespaceBefore(text, info.Members[0].KeyStart)
            ? LineIndent(text, info.Members[0].KeyStart)
            : objectIndent + "  ";
        var entry = $"{JsonSerializer.Serialize(key)}: {Render(node, childIndent, eol)}";

        if (info.Members.Count == 0)
        {
            var inner = text.AsSpan(info.Open + 1, info.Close - info.Open - 1);
            if (inner.IsWhiteSpace())
                return text[..(info.Open + 1)] + eol + childIndent + entry + eol + objectIndent + text[info.Close..];
            return text.Insert(info.Open + 1, eol + childIndent + entry);
        }

        var tail = info.Members[^1];
        return tail.Comma >= 0
            ? text.Insert(tail.Comma + 1, eol + childIndent + entry + ",")
            : text.Insert(tail.ValueEnd, "," + eol + childIndent + entry);
    }

    private static string RemoveMember(string text, ObjectInfo parent, Member member)
    {
        var index = parent.Members.IndexOf(member);
        var lineStart = LineStart(text, member.KeyStart);
        var ownsLine = OnlyWhitespaceBefore(text, member.KeyStart);
        var start = ownsLine ? lineStart : member.KeyStart;
        var end = member.Comma >= 0 ? member.Comma + 1 : member.ValueEnd;

        if (ownsLine)
        {
            var p = end;
            while (p < text.Length && text[p] is ' ' or '\t') p++;
            if (p < text.Length && text[p] == '\r') p++;
            if (p < text.Length && text[p] == '\n') end = p + 1;
        }

        var result = text.Remove(start, end - start);
        if (member.Comma < 0 && index > 0 && parent.Members[index - 1].Comma >= 0)
            result = result.Remove(parent.Members[index - 1].Comma, 1);
        return result;
    }

    private static (ObjectInfo Parent, Member Member)? Locate(string text, IReadOnlyList<string> path)
    {
        var start = SkipTrivia(text, 0);
        if (start >= text.Length || text[start] != '{') return null;
        var info = ParseObject(text, start);
        for (var i = 0; i < path.Count; i++)
        {
            var member = info.Members.FirstOrDefault(m => string.Equals(m.Key, path[i], StringComparison.OrdinalIgnoreCase));
            if (member is null) return null;
            if (i == path.Count - 1) return (info, member);
            if (text[member.ValueStart] != '{') return null;
            info = ParseObject(text, member.ValueStart);
        }
        return null;
    }

    private static JsonNode Nest(IReadOnlyList<string> path, int from, JsonNode value)
    {
        var node = value;
        for (var i = path.Count - 1; i >= from; i--) node = new JsonObject { [path[i]] = node };
        return node;
    }

    private static string Render(JsonNode node, string indent, string eol)
    {
        var json = node.ToJsonString(SettingsJson.Indented).Replace("\r\n", "\n");
        return json.Replace("\n", eol + indent);
    }

    private static string Splice(string text, int start, int end, string replacement) =>
        string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));

    private static string Eol(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static int LineStart(string text, int position)
    {
        var i = position;
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }

    private static bool OnlyWhitespaceBefore(string text, int position)
    {
        for (var i = LineStart(text, position); i < position; i++)
            if (text[i] is not (' ' or '\t')) return false;
        return true;
    }

    private static string LineIndent(string text, int position)
    {
        var i = LineStart(text, position);
        var builder = new StringBuilder();
        while (i < text.Length && text[i] is ' ' or '\t') builder.Append(text[i++]);
        return builder.ToString();
    }

    private static void Verify(string text)
    {
        try { JsonNode.Parse(text, documentOptions: SettingsJson.DocumentOptions); }
        catch (JsonException ex) { throw new FormatException($"The edit would produce invalid JSON: {ex.Message}"); }
    }

    private static int SkipTrivia(string t, int p)
    {
        while (p < t.Length)
        {
            if (char.IsWhiteSpace(t[p])) p++;
            else if (t[p] == '/' && p + 1 < t.Length && t[p + 1] == '/')
            {
                while (p < t.Length && t[p] != '\n') p++;
            }
            else if (t[p] == '/' && p + 1 < t.Length && t[p + 1] == '*')
            {
                var end = t.IndexOf("*/", p + 2, StringComparison.Ordinal);
                if (end < 0) throw new FormatException("Unterminated comment.");
                p = end + 2;
            }
            else break;
        }
        return p;
    }

    private static int SkipString(string t, int p)
    {
        p++;
        while (p < t.Length)
        {
            if (t[p] == '\\') p += 2;
            else if (t[p] == '"') return p + 1;
            else p++;
        }
        throw new FormatException("Unterminated string.");
    }

    private static int SkipValue(string t, int p)
    {
        if (p >= t.Length) throw new FormatException("Unexpected end of file.");
        switch (t[p])
        {
            case '"': return SkipString(t, p);
            case '{': return ParseObject(t, p).Close + 1;
            case '[':
                p++;
                while (true)
                {
                    p = SkipTrivia(t, p);
                    if (p >= t.Length) throw new FormatException("Unterminated array.");
                    if (t[p] == ']') return p + 1;
                    p = SkipValue(t, p);
                    p = SkipTrivia(t, p);
                    if (p < t.Length && t[p] == ',') p++;
                }
            default:
                while (p < t.Length && t[p] is not (',' or '}' or ']') && !char.IsWhiteSpace(t[p]) &&
                       !(t[p] == '/' && p + 1 < t.Length && t[p + 1] is '/' or '*')) p++;
                return p;
        }
    }

    private static ObjectInfo ParseObject(string t, int open)
    {
        var members = new List<Member>();
        var p = open + 1;
        while (true)
        {
            p = SkipTrivia(t, p);
            if (p >= t.Length) throw new FormatException("Unterminated object.");
            if (t[p] == '}') return new ObjectInfo(open, p, members);
            if (t[p] != '"') throw new FormatException($"Expected a property name at offset {p}.");

            var keyStart = p;
            var keyEnd = SkipString(t, p);
            var key = JsonSerializer.Deserialize<string>(t.AsSpan(keyStart, keyEnd - keyStart)) ?? "";
            p = SkipTrivia(t, keyEnd);
            if (p >= t.Length || t[p] != ':') throw new FormatException($"Expected ':' at offset {p}.");
            p = SkipTrivia(t, p + 1);

            var valueStart = p;
            var valueEnd = SkipValue(t, p);
            var next = SkipTrivia(t, valueEnd);
            var comma = -1;
            if (next < t.Length && t[next] == ',')
            {
                comma = next;
                p = next + 1;
            }
            else p = valueEnd;
            members.Add(new Member(key, keyStart, valueStart, valueEnd, comma));
        }
    }
}
