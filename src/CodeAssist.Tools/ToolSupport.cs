using System.Security.Cryptography;
using System.Text;
using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;

namespace CodeAssist.Tools;

public static class ToolSupport
{
    public static ResolvedPath Resolve(string path, ToolContext context)
    {
        try { return new PathGuard(context.Workspace, context.ProtectedGlobs).Resolve(path); }
        catch (PathRejectedException exception) { throw new ToolArgumentException($"Path '{path}' was rejected: {exception.Message}"); }
    }

    public static ToolEffect Effect(ToolEffectKind kind, ResolvedPath path) => new(kind, path.Relative, path.Outside, path.Protected);

    public static string Describe(ResolvedPath path) => path.Outside ? path.Full : path.Relative;

    public static ToolResult BoundedText(string text, ToolContext context)
    {
        var output = OutputBudget.Head(text, context.Limits.OutputMaxLines, context.Limits.OutputMaxBytes);
        return ToolResult.Ok(output.Truncated ? $"{output.Text}\n[output truncated]" : output.Text.TrimEnd());
    }

    public static async Task<ToolResult> FromPortAsync(Func<Task<ToolResult>> run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await run();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not OutOfMemoryException)
        {
            return ToolResult.Error($"Service operation failed: {exception.Message}");
        }
    }
}

/// <param name="Hash">SHA-256 of the raw bytes, lowercase hex. Used for the optional <c>expectHash</c> precondition.</param>
internal sealed record TextFile(string Text, bool Bom, string Hash, byte[] Bytes);

internal static class TextFiles
{
    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Encode(string text, bool bom)
    {
        var body = Encoding.UTF8.GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    /// <summary>Reads an existing UTF-8 text file within the size limit. The error is a model-facing message.</summary>
    public static async Task<(TextFile? File, string? Error)> ReadAsync(IFileSystem fileSystem, ResolvedPath path, ToolLimits limits, CancellationToken cancellationToken)
    {
        var shown = ToolSupport.Describe(path);
        var facts = fileSystem.Stat(path.Full);
        if (facts is null) return (null, $"Not found: {shown}");
        if (facts.IsDirectory) return (null, $"{shown} is a directory.");
        if (facts.Length > limits.MaxFileBytes) return (null, $"{shown} is {facts.Length:N0} bytes, over the {limits.MaxFileBytes:N0} byte limit.");

        var bytes = await fileSystem.ReadAllBytesAsync(path.Full, cancellationToken);
        if (ContentSniffer.IsBinary(bytes.AsSpan(0, Math.Min(bytes.Length, ContentSniffer.HeadBytes)))) return (null, $"{shown} is a binary file.");

        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            var text = Strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            return (new TextFile(text, bom, Hash(bytes), bytes), null);
        }
        catch (DecoderFallbackException)
        {
            return (null, $"{shown} is not valid UTF-8 text.");
        }
    }

    public static string? CheckHash(string? expected, TextFile file, string shown) =>
        expected is not null && !string.Equals(expected, file.Hash, StringComparison.OrdinalIgnoreCase)
            ? $"{shown} changed since it was read (current sha256 {file.Hash}). Read it again before editing."
            : null;

    public static string? CheckSize(string text, ToolLimits limits, string shown)
    {
        var size = Encoding.UTF8.GetByteCount(text);
        return size > limits.MaxFileBytes ? $"The new content of {shown} is {size:N0} bytes, over the {limits.MaxFileBytes:N0} byte limit." : null;
    }
}
