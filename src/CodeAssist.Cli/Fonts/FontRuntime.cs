using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Spectre.Console;

namespace CodeAssist.Cli.Fonts;

public sealed class HttpFontSource(HttpClient? client = null, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null) : IFontSource
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public Task<Stream> OpenAsync(CancellationToken cancellation) => Client.GetStreamAsync(FontPin.Url, cancellation);

    public async Task<IReadOnlyList<FontPack>> CatalogAsync(CancellationToken cancellation) =>
        FontCatalog.Parse(await ReadDocumentAsync(FontCatalog.MetadataUrl, 2 * 1024 * 1024, cancellation));

    public Task<Stream> OpenAsync(FontPack pack, CancellationToken cancellation)
    {
        if (!pack.IsTextFamily || pack.Archive.Length is < 5 or > 100 || pack.Archive[..^4].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            !pack.Archive.EndsWith(".zip", StringComparison.Ordinal) || pack.Size is <= 0 or > FontCatalog.MaximumArchiveBytes || pack.Digest is null)
            throw new InvalidDataException("Untrusted font asset.");
        return OpenTrustedAsync(pack.Url, true, cancellation);
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> NoticesAsync(CancellationToken cancellation)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var notice in FontNotices.Sources)
        {
            var bytes = await ReadDocumentAsync(notice.Url, 1024 * 1024, cancellation);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(bytes), Convert.FromHexString(notice.Sha256)))
                throw new InvalidDataException("Glyph notice integrity check failed.");
            result.Add(notice.Name, bytes);
        }
        return result;
    }

    private async Task<byte[]> ReadDocumentAsync(string url, int bound, CancellationToken cancellation)
    {
        await using var input = await OpenTrustedAsync(url, false, cancellation);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, bound + 1 - (int)output.Length)), cancellation);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > bound) throw new InvalidDataException("Official metadata exceeds size limit.");
        }
    }

    private async Task<Stream> OpenTrustedAsync(string url, bool releaseAsset, CancellationToken cancellation)
    {
        var address = new Uri(url);
        for (var hop = 0; hop <= 4; hop++)
        {
            if (!AllowedAddress(address, releaseAsset, hop > 0)) throw new InvalidDataException("Untrusted font source or redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("CodeAssist-Fonts/3.5.1");
            request.Headers.Accept.ParseAdd(releaseAsset ? "application/octet-stream" : "application/vnd.github+json");
            var response = send is not null ? await send(request, cancellation) :
                await (client ?? Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.RequestMessage?.RequestUri is { } effective && effective != address)
            {
                response.Dispose();
                throw new InvalidDataException("Automatic redirects must be disabled for font sources.");
            }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || hop == 4) throw new InvalidDataException("Invalid font redirect.");
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellation), response);
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Font redirect limit exceeded.");
    }

    public static bool AllowedAddress(Uri address, bool releaseAsset, bool redirect)
    {
        if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length != 0 || address.Fragment.Length != 0) return false;
        if (releaseAsset)
        {
            if (address.Host == "github.com") return address.AbsoluteUri.StartsWith(FontCatalog.ReleaseRoot, StringComparison.Ordinal);
            return redirect && (address.Host == "release-assets.githubusercontent.com" || address.Host == "objects.githubusercontent.com") &&
                address.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal);
        }
        return !redirect && (address.AbsoluteUri == FontCatalog.MetadataUrl || FontNotices.Sources.Any(notice => notice.Url == address.AbsoluteUri));
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default) => inner.ReadAsync(buffer, cancellation);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) response.Dispose(); base.Dispose(disposing); }
    }
}

public sealed class SpectreFontInteraction(IAnsiConsole console, FontPaths paths) : IFontInteraction
{
    public async Task<FontPack?> PickAsync(IReadOnlyList<FontPack> catalog, CancellationToken cancellation)
    {
        var query = "";
        var selected = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var matches = catalog.Where(pack => pack.Matches(query)).ToArray();
            var width = Math.Max(1, console.Profile.Width);
            void Line(string value) => console.WriteLine(value.Length <= width ? value : value[..Math.Max(0, width - 1)] + ">" );
            console.Clear();
            Line("Terminal icons / " + FontCatalog.Version);
            Line("Search Nerd Fonts: " + query);
            Line(matches.Length + " of " + catalog.Count + " packs");
            if (matches.Length == 0) Line("No matching fonts");
            var start = Math.Max(0, selected - 5);
            foreach (var index in Enumerable.Range(start, Math.Min(6, matches.Length - start)))
                Line((selected == index ? "> " : "  ") + matches[index]);
            ConsoleKeyInfo? key;
            try { key = await console.Input.ReadKeyAsync(true, cancellation); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new FontSetupExitException(); }
            if (key is null || key.Value.Key == ConsoleKey.Escape) return null;
            if (key.Value.Key == ConsoleKey.C && key.Value.Modifiers.HasFlag(ConsoleModifiers.Control)) throw new FontSetupExitException();
            if (key.Value.Key == ConsoleKey.Enter)
            {
                if (matches.Length == 0) continue;
                if (matches[selected].IsTextFamily) return matches[selected];
            }
            else if (key.Value.Key == ConsoleKey.UpArrow) selected = Math.Max(0, selected - 1);
            else if (key.Value.Key == ConsoleKey.DownArrow) selected = Math.Min(matches.Length - 1, selected + 1);
            else if (key.Value.Key == ConsoleKey.Home) selected = 0;
            else if (key.Value.Key == ConsoleKey.End) selected = matches.Length - 1;
            else if (key.Value.Key == ConsoleKey.Backspace) { if (query.Length > 0) query = query[..^1]; selected = 0; }
            else if (!char.IsControl(key.Value.KeyChar) && query.Length < 96) { query += key.Value.KeyChar; selected = 0; }
        }
    }

    public async Task<bool> RetryCatalogAsync(CancellationToken cancellation) => await ChooseAsync(new SelectionPrompt<string>()
        .Title("Font catalog unavailable").AddChoices("Exit startup", "Retry catalog"), cancellation) == "Retry catalog";

    public async Task<bool> OfferAsync(FontPack pack, CancellationToken cancellation)
    {
        Report(pack.ToString());
        Report("Install target: " + paths.TargetDirectory);
        Report("Official source: " + pack.Url);
        Report("Preserve selected archive and separate glyph notices beside installation. Licenses vary; upstream audit flags an unlicensed glyph set.");
        return await ChooseAsync(new SelectionPrompt<string>().Title("Install missing selected font for this user?")
            .AddChoices("Exit startup", "Download selected font"), cancellation) == "Download selected font";
    }
    public async Task<bool> OfferAsync(CancellationToken cancellation)
    {
        Report("Terminal icons / " + FontPin.Family);
        Report("Install the missing pinned Nerd Font for this user? Nerd Font is required for interactive chat.");
        Report("Install target: " + paths.TargetDirectory);
        Report("Official source: " + FontPin.Url);
        Report("Font and glyph licenses vary. Selected installation retains separate notices.");
        return await ChooseAsync(new SelectionPrompt<string>()
            .Title("Automatic font setup")
            .AddChoices("Exit startup", "Start automatic setup"), cancellation) == "Start automatic setup";
    }

    public async Task<bool> ConfirmAsync(CancellationToken cancellation)
    {
        Report("\ueb08 CodeAssist  \uea76 Close  \uea60 New  \uec0f Send");
        Report("\uf15c Read  \uf044 Edit  \uf489 Terminal  \uf07b Folder");
        return await ChooseAsync(new SelectionPrompt<string>()
            .Title("Confirm glyphs in this terminal")
            .AddChoices("Exit startup", "Glyphs look correct"), cancellation) == "Glyphs look correct";
    }

    private async Task<string> ChooseAsync(SelectionPrompt<string> prompt, CancellationToken cancellation)
    {
        try { return await prompt.ShowAsync(console, cancellation); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new FontSetupExitException(); }
    }

    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellation)
    {
        Report("Escape cancels font setup; Ctrl+C exits chat.");
        using var setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<ConsoleKeyInfo?>? pending = null;
        var running = operation(setupCancellation.Token);
        try
        {
            while (!running.IsCompleted)
            {
                pending ??= console.Input.ReadKeyAsync(true, inputCancellation.Token);
                if (await Task.WhenAny(running, pending) == running) break;
                ConsoleKeyInfo? key;
                try { key = await pending; }
                catch (OperationCanceledException) when (!inputCancellation.IsCancellationRequested)
                {
                    throw new FontSetupExitException();
                }
                pending = null;
                if (key is { Key: ConsoleKey.C } && key.Value.Modifiers.HasFlag(ConsoleModifiers.Control))
                    throw new FontSetupExitException();
                if (key is null || key.Value.Key == ConsoleKey.Escape)
                {
                    setupCancellation.Cancel();
                    break;
                }
            }
            await running;
            cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            setupCancellation.Cancel();
            inputCancellation.Cancel();
            if (pending is not null)
            {
                try { await pending; }
                catch (OperationCanceledException) { }
            }
            try { await running; }
            catch (Exception) when (running.IsCanceled || running.IsFaulted) { }
        }
    }

    public void Report(string message) => console.WriteLine(message);
}

public sealed class FontFiles : IFontFiles
{
    public bool Exists(string path) => File.Exists(path);

    public Stream OpenRead(string path)
    {
        EnsureNotLinked(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public IEnumerable<string> EnumerateFonts(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        EnsureNotLinked(directory);
        return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 8
        }).Where(path => Path.GetExtension(path).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetExtension(path).Equals(".otf", StringComparison.OrdinalIgnoreCase));
    }

    public void CreateDirectory(string path)
    {
        EnsureNotLinked(path);
        Directory.CreateDirectory(path);
        EnsureNotLinked(path);
    }

    public void WriteNew(string path, byte[] bytes)
    {
        EnsureNotLinked(path);
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
        }
    }

    public void MoveNew(string source, string destination)
    {
        EnsureNotLinked(source);
        EnsureNotLinked(destination);
        File.Move(source, destination, overwrite: false);
    }

    public void Delete(string path)
    {
        EnsureNotLinked(path);
        File.Delete(path);
    }

    public void WriteAtomic(string path, byte[] bytes)
    {
        EnsureNotLinked(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        WriteNew(temporary, bytes);
        try
        {
            EnsureNotLinked(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    public async Task RefreshAsync(FontPlatform platform, string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            if (platform == FontPlatform.Windows && OperatingSystem.IsWindows()) RegisterWindows(path);
            else if (platform == FontPlatform.MacOS && OperatingSystem.IsMacOS()) RegisterMac(path);
            else if (platform == FontPlatform.Linux && OperatingSystem.IsLinux())
            {
                var executable = new[] { "/usr/bin/fc-cache", "/bin/fc-cache" }.FirstOrDefault(File.Exists);
                if (executable is null) return;
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        ArgumentList = { "-f", Path.GetDirectoryName(path)! }
                    };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    if (!process.Start()) throw new IOException("Font cache refresh could not start.");
                    try
                    {
                        var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                        await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
                        if (process.ExitCode != 0) throw new IOException("Font cache refresh failed.");
                    }
                    catch
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        throw;
                    }
                }
            }
            else throw new IOException("Font registration is unavailable on this platform.");
        }
        catch (Exception exception) when (exception is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException("Current-user font registration is unavailable.", exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(string path)
    {
        const string keyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
        using var stream = File.OpenRead(path);
        var family = TrueTypeFont.Describe(stream, strict: true) ?? throw new IOException("Registered font family is invalid.");
        var name = "CodeAssist " + family + " Regular (TrueType)";
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true) ?? throw new IOException("Current-user font registry is unavailable.");
        var existing = key.GetValue(name);
        if (existing is not null && !string.Equals(existing as string, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("An existing font registration will not be overwritten.");
        var added = false;
        try
        {
            if (existing is null) key.SetValue(name, path, RegistryValueKind.String);
            if (AddFontResourceEx(path, 0, IntPtr.Zero) == 0) throw new IOException("Current-session font registration failed.");
            added = true;
            SendMessageTimeout(new IntPtr(0xffff), 0x001d, UIntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out _);
        }
        catch
        {
            if (added) RemoveFontResourceEx(path, 0, IntPtr.Zero);
            if (existing is null) key.DeleteValue(name, throwOnMissingValue: false);
            throw;
        }
    }

    [SupportedOSPlatform("macos")]
    private static void RegisterMac(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, bytes, bytes.Length, false);
        if (url == IntPtr.Zero) throw new IOException("Font URL creation failed.");
        try
        {
            if (!CTFontManagerRegisterFontsForURL(url, 1, out var error))
            {
                if (error != IntPtr.Zero) CFRelease(error);
                throw new IOException("Current-user font registration failed.");
            }
        }
        finally { CFRelease(url); }
    }

    private static void EnsureNotLinked(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Font setup does not follow linked paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void TryDelete(string path)
    {
        try { Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("gdi32.dll", EntryPoint = "AddFontResourceExW", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string path, uint flags, IntPtr reserved);
    [DllImport("gdi32.dll", EntryPoint = "RemoveFontResourceExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveFontResourceEx(string path, uint flags, IntPtr reserved);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr parameter, IntPtr data, uint flags, uint timeout, out UIntPtr result);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] bytes, nint length, [MarshalAs(UnmanagedType.I1)] bool directory);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
    [DllImport("/System/Library/Frameworks/CoreText.framework/CoreText")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CTFontManagerRegisterFontsForURL(IntPtr url, uint scope, out IntPtr error);
}