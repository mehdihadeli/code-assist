namespace CodeAssist.Configuration;

/// <summary>A model reference: <c>provider/model</c> or <c>provider/model@variant</c>. A bare model name uses provider "default". Model ids may contain '/'.</summary>
public sealed record ModelRef(string Provider, string Model, string? Variant = null)
{
    public const string DefaultProvider = "default";

    public static bool TryParse(string? text, out ModelRef reference)
    {
        reference = new ModelRef(DefaultProvider, "");
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        string? variant = null;
        var at = value.LastIndexOf('@');
        if (at > 0)
        {
            variant = value[(at + 1)..];
            value = value[..at];
            if (variant.Length == 0) return false;
        }

        var slash = value.IndexOf('/');
        if (slash == 0) return false;
        var provider = slash > 0 ? value[..slash] : DefaultProvider;
        var model = slash > 0 ? value[(slash + 1)..] : value;
        if (model.Length == 0) return false;

        reference = new ModelRef(provider, model, variant);
        return true;
    }

    public override string ToString() => $"{Provider}/{Model}" + (Variant is null ? "" : $"@{Variant}");
}
