namespace CodeAssist.Configuration;

public static class EnvironmentReference
{
    public static bool TryParse(string? value, out string variable)
    {
        variable = "";
        if (value is null || !value.StartsWith("{env:", StringComparison.Ordinal) || !value.EndsWith('}') || value.Length is < 7 or > 262)
            return false;
        var name = value.AsSpan(5, value.Length - 6);
        if (!char.IsAsciiLetter(name[0]) && name[0] != '_') return false;
        foreach (var character in name)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.' or '/')) return false;
        variable = name.ToString();
        return true;
    }
}