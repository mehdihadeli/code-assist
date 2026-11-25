using System.Text;

namespace CodeAssist.Tools;

internal static class PagedTextLines
{
    public static IEnumerable<string> Read(Stream stream, int maximumLineChars, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        long scanned = 0;
        var clipped = false;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((scanned += count) > 64L * 1024 * 1024) throw new IOException("Paged read exceeds its 64 MiB scan ceiling.");
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    yield return line.ToString().TrimEnd('\r') + (clipped ? " [line clipped]" : "");
                    line.Clear();
                    clipped = false;
                }
                else if (line.Length < maximumLineChars) line.Append(character);
                else clipped = true;
            }
        }
        if (line.Length > 0 || clipped) yield return line.ToString().TrimEnd('\r') + (clipped ? " [line clipped]" : "");
    }
}