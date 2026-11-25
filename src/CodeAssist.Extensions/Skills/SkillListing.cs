using CodeAssist.Tools;
using System.Text;
using System.Text.Json;

namespace CodeAssist.Extensions.Skills;

public static class SkillListing
{
    public static bool ValidId(string id) => id.Length is > 0 and <= 240 && id.Split('/') is { Length: <= 2 } parts &&
        parts.All(part => part.Length > 0 && part is not "." and not ".." && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'));

    public static string Render(IEnumerable<SkillSummary> skills)
    {
        var builder = new StringBuilder();
        foreach (var skill in skills)
        {
            var entry = JsonSerializer.Serialize(new { skill.Id, skill.Description, skill.Parameters, skill.Focus }) + "\n";
            if (Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(entry) > 12_288)
            {
                builder.Append("Additional skills omitted from advertisement; request the scoped index.");
                break;
            }
            builder.Append(entry);
        }
        return builder.ToString();
    }
}