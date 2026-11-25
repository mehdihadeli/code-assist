namespace CodeAssist.Workspaces.Platform;

public static class LimitedStreamReader
{
    public static async Task<byte[]> ReadAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (content.Length + count > maxBytes) throw new IOException("Stream exceeds its byte budget.");
            content.Write(buffer, 0, count);
        }
        return content.ToArray();
    }
}