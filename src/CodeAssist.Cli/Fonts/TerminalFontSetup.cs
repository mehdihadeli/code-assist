using System.Security.Cryptography;

namespace CodeAssist.Cli.Fonts;

public interface ITerminalFontSetup
{
    Task<bool> ConfigureAsync(string icons, CancellationToken cancellation);
}

public interface IFontPreferences
{
    string? Read();
    void Write(string value);
}

public interface IFontHost
{
    bool IsLocal { get; }
    Task<bool> IsInstalledAsync(CancellationToken cancellation);
    Task RegisterAsync(byte[] payload, CancellationToken cancellation);
    Task<string?> FindInstalledAsync(string? family, FontPack? pack, CancellationToken cancellation) => Task.FromResult<string?>(null);
    Task RegisterAsync(FontInstallation installation, CancellationToken cancellation) => throw new NotSupportedException();
}

public interface IFontSource
{
    Task<Stream> OpenAsync(CancellationToken cancellation);
    Task<IReadOnlyList<FontPack>> CatalogAsync(CancellationToken cancellation) => throw new NotSupportedException();
    Task<Stream> OpenAsync(FontPack pack, CancellationToken cancellation) => throw new NotSupportedException();
    Task<IReadOnlyDictionary<string, byte[]>> NoticesAsync(CancellationToken cancellation) => throw new NotSupportedException();
}

public interface IFontInteraction
{
    Task<bool> OfferAsync(CancellationToken cancellation);
    Task<bool> ConfirmAsync(CancellationToken cancellation);
    Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellation);
    void Report(string message);
    Task<FontPack?> PickAsync(IReadOnlyList<FontPack> catalog, CancellationToken cancellation) => Task.FromResult<FontPack?>(null);
    Task<bool> OfferAsync(FontPack pack, CancellationToken cancellation) => OfferAsync(cancellation);
    Task<bool> RetryCatalogAsync(CancellationToken cancellation) => Task.FromResult(false);
}

public sealed class FontSetupExitException : Exception;

public sealed record FontPin(int Length, string Sha256)
{
    public const string Family = "JetBrainsMono Nerd Font Mono";
    public const string Url = "https://raw.githubusercontent.com/ryanoasis/nerd-fonts/fa7b859994228a9c8759f99c55a8d31ee92a1b5e/patched-fonts/JetBrainsMono/Ligatures/Regular/JetBrainsMonoNerdFontMono-Regular.ttf";
    public static FontPin Official { get; } = new(2470116, "f01031f40e48dc29e1112e6b0b0450a2c6cd097f3f35cfff05c55cb311f8034c");

    public async Task<byte[]> VerifyAsync(Stream stream, CancellationToken cancellation)
    {
        if (Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid font size bound.");
        var payload = new byte[Length];
        await stream.ReadExactlyAsync(payload, cancellation);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellation) != 0 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), Convert.FromHexString(Sha256)))
            throw new InvalidDataException("Font integrity check failed.");
        return payload;
    }
}

public sealed class TerminalFontSetup(
    IFontHost host, IFontPreferences preferences, IFontSource source, IFontInteraction interaction, FontPin? pin, TimeProvider time) : ITerminalFontSetup
{
    public async Task<bool> ConfigureAsync(string icons, CancellationToken cancellation)
    {
        if (icons is not ("auto" or "nerd")) throw new ArgumentException("Nerd Font is required. Use --icons auto or nerd.", nameof(icons));
        if (pin is null) return await ConfigureCatalogAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!host.IsLocal)
        {
            interaction.Report("Set up a Nerd Font on the terminal host for SSH, WSL, containers or headless sessions. Interactive startup stopped.");
            return false;
        }
        try
        {
            var installed = await host.IsInstalledAsync(cancellation);
            if (installed)
            {
                preferences.Write("v1:nerd");
                return true;
            }
            if (!installed)
            {
                if (!await interaction.OfferAsync(cancellation)) return false;
                await interaction.RunAsync(async setupCancellation =>
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45), time);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(setupCancellation, deadline.Token);
                    interaction.Report("Downloading pinned official font; verifying SHA-256 before registration.");
                    await using var stream = await source.OpenAsync(timeout.Token);
                    var payload = await pin.VerifyAsync(stream, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    interaction.Report("Font verified. Registering for the current user without elevation.");
                    await host.RegisterAsync(payload, timeout.Token);
                }, cancellation);
            }
            interaction.Report("Select " + FontPin.Family + " in your terminal. Terminal profiles were not modified. Reopen the terminal if needed; retry with --icons nerd.");
            preferences.Write("v1:nerd");
            interaction.Report("Nerd Font installed. Nerd icons enabled.");
            return true;
        }
        catch (FontSetupExitException)
        {
            throw new OperationCanceledException(cancellation);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            interaction.Report("Font setup canceled or timed out. Interactive startup stopped; retry with --icons nerd.");
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or System.Security.SecurityException or FormatException)
        {
            interaction.Report("Font setup unavailable. Interactive startup stopped; retry with --icons nerd.");
            return false;
        }
    }

    private async Task<bool> ConfigureCatalogAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!host.IsLocal)
        {
            interaction.Report("Set up a Nerd Font on the terminal host for SSH, WSL, containers or headless sessions. Interactive startup stopped.");
            return false;
        }
        try
        {
            var saved = preferences.Read();
            var confirmedFamily = FontPreferences.ConfirmedFamily(saved);
            var found = confirmedFamily is null ? null : await host.FindInstalledAsync(confirmedFamily, null, cancellation);
            if (string.IsNullOrEmpty(found)) found = await host.FindInstalledAsync(null, null, cancellation);
            if (!string.IsNullOrEmpty(found))
            {
                preferences.Write(FontPreferences.Confirm(found));
                return true;
            }
            FontPack? selected = null;
            string? family = null;
            if (string.IsNullOrEmpty(family))
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    IReadOnlyList<FontPack>? catalog = null;
                    try
                    {
                        interaction.Report("Loading official Nerd Font catalog / " + FontCatalog.Version);
                        await interaction.RunAsync(async token =>
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45), time);
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                            catalog = await source.CatalogAsync(linked.Token);
                        }, cancellation);
                        selected = await interaction.PickAsync(catalog!, cancellation);
                        break;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException)
                    {
                        interaction.Report("Font catalog unavailable. Interactive startup stopped.");
                        if (attempt == 2 || !await interaction.RetryCatalogAsync(cancellation)) return false;
                    }
                }
                if (selected is null || !selected.IsTextFamily) return false;
                family = await host.FindInstalledAsync(null, selected, cancellation);
                if (string.IsNullOrEmpty(family))
                {
                    if (selected.Digest is null || selected.Size is <= 0 or > FontCatalog.MaximumArchiveBytes)
                    {
                        interaction.Report("Trusted SHA-256 or bounded archive unavailable. Interactive startup stopped.");
                        return false;
                    }
                    if (!await interaction.OfferAsync(selected, cancellation)) return false;
                    await interaction.RunAsync(async token =>
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5), time);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                        interaction.Report("Downloading " + selected.Archive + "; verifying published SHA-256 before extraction.");
                        await using var input = await source.OpenAsync(selected, linked.Token);
                        var temporary = Path.Combine(Path.GetTempPath(), "codeassist-font-" + Guid.NewGuid().ToString("N") + ".zip");
                        await using var archive = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                        await FontCatalog.VerifyArchiveAsync(selected, input, archive, linked.Token);
                        var installation = await FontArchive.ExtractAsync(selected, archive, linked.Token);
                        var notices = new Dictionary<string, byte[]>(installation.Notices);
                        foreach (var notice in await source.NoticesAsync(linked.Token)) notices.Add("glyphs/" + notice.Key, notice.Value);
                        if (notices.Count == installation.Notices.Count) throw new InvalidDataException("Glyph attribution unavailable.");
                        installation = installation with { Notices = notices };
                        interaction.Report("Font verified. Registering for the current user without elevation.");
                        await host.RegisterAsync(installation, linked.Token);
                        family = installation.Family;
                    }, cancellation);
                }
                else interaction.Report("Selected compatible font already installed; no font download.");
            }
            interaction.Report("Select " + family + " in your terminal. Terminal profiles were not modified. Reopen the terminal if needed; retry with --icons nerd.");
            preferences.Write(FontPreferences.Confirm(family!));
            interaction.Report("Nerd Font available. Nerd icons enabled.");
            return true;
        }
        catch (FontSetupExitException) { throw new OperationCanceledException(cancellation); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            interaction.Report("Font setup canceled or timed out. Interactive startup stopped; retry with --icons nerd.");
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or
            System.Security.SecurityException or FormatException or System.Text.Json.JsonException or ArgumentException or OverflowException)
        {
            interaction.Report("Font setup unavailable. Interactive startup stopped; retry with --icons nerd.");
            return false;
        }
    }
}