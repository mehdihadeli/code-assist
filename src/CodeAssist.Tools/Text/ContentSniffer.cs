namespace CodeAssist.Tools.Text;

/// <summary>Cheap content sniffing from the first bytes of a file.</summary>
public static class ContentSniffer
{
    public const int HeadBytes = 8192;

    /// <summary>A NUL byte means binary. UTF-16 text is treated as binary on purpose: tools only handle UTF-8.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) >= 0;

    /// <summary>MIME type of media the model can receive as an attachment, or null for anything else.</summary>
    public static string? AttachmentMime(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "image/png";
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (head.StartsWith("GIF8"u8)) return "image/gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (head.StartsWith("%PDF-"u8)) return "application/pdf";
        return null;
    }
}
