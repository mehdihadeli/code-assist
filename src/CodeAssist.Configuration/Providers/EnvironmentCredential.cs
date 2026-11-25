using System.Text.RegularExpressions;

namespace CodeAssist.Configuration;

public static class EnvironmentCredential
{
    public static bool IsProviderKey(IReadOnlyList<string> path) => path.Count == 3 &&
        string.Equals(path[0], "providers", StringComparison.OrdinalIgnoreCase) && string.Equals(path[2], "apiKey", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetVariable(string? value, out string variable)
    {
        var match = Regex.Match(value ?? "", @"\A\{env:([A-Za-z_][A-Za-z0-9_]{0,255})\}\z", RegexOptions.CultureInvariant);
        variable = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }

    public static string? Resolve(string? value, IReadOnlyDictionary<string, string> environment)
    {
        if (TryGetVariable(value, out var variable))
        {
            if (!environment.TryGetValue(variable, out var secret) || string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException($"Credential environment variable '{variable}' is missing or empty.");
            if (secret.StartsWith("{env:", StringComparison.Ordinal))
                throw new InvalidOperationException("Credential environment references cannot be recursive.");
            return secret;
        }
        if (value?.Contains("{env:", StringComparison.Ordinal) == true)
            throw new InvalidOperationException("Credential references must be exactly {env:VARIABLE_NAME}.");
        return value;
    }
}