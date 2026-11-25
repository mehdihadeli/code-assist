using CodeAssist.Cli.Fonts;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Shouldly;

namespace CodeAssist.Cli.IntegrationTests.Commands;

public sealed class TerminalFontSetupTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly FontPin TestPin = new(4, "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a");

    [Fact]
    public async Task Should_CancelStalledDownloadAtDeadline_When_FortyFiveSecondsElapse()
    {
        var clock = new ManualTimeProvider();
        var rig = new Rig(time: clock);
        var downloadCancellation = CancellationToken.None;
        rig.Source.OpenAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            downloadCancellation = call.Arg<CancellationToken>();
            await Task.Delay(Timeout.Infinite, downloadCancellation);
            return Stream.Null;
        });
        var result = rig.Setup.ConfigureAsync("auto", Cancellation);
        result.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(44));
        downloadCancellation.IsCancellationRequested.ShouldBeFalse();
        result.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        downloadCancellation.IsCancellationRequested.ShouldBeTrue();
        (await result).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_NeverSwallowChatExit_When_FontInteractionSignalsControlC()
    {
        var rig = new Rig();
        rig.Interaction.OfferAsync(Arg.Any<CancellationToken>()).Returns<bool>(_ => throw new FontSetupExitException());
        await Should.ThrowAsync<OperationCanceledException>(() => rig.Setup.ConfigureAsync("auto", Cancellation));
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_RegisterVerifiedBytesAndPersistChoice_When_UserConsents()
    {
        var rig = new Rig();
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        await rig.Host.Received(1).RegisterAsync(Arg.Is<byte[]>(bytes => bytes.SequenceEqual(Bytes)), Arg.Any<CancellationToken>());
        rig.Saved.ShouldBe("v1:nerd");
        rig.Stream.Disposed.ShouldBeTrue();
        rig.Messages.ShouldContain(message => message.Contains("verified", StringComparison.Ordinal));
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task Should_LeavePreferenceUnchangedWithoutRegistration_When_IntegrityFails(string partition)
    {
        var rig = new Rig(partition switch { "hash" => [4, 3, 2, 1], "short" => [1, 2, 3], _ => [1, 2, 3, 4, 5] });
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        rig.Stream.Disposed.ShouldBeTrue();
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_StopAtOneByteOverBound_When_StreamIsOversized()
    {
        await using var stream = new TrackingStream(new byte[1000]);
        await Should.ThrowAsync<InvalidDataException>(() => TestPin.VerifyAsync(stream, Cancellation));
        stream.Position.ShouldBe(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4194305)]
    public async Task Should_RejectInvalidAllocationBound_When_PinIsInvalid(int length)
    {
        await using var stream = new MemoryStream(Bytes);
        await Should.ThrowAsync<InvalidDataException>(() => new FontPin(length, TestPin.Sha256).VerifyAsync(stream, Cancellation));
        stream.Position.ShouldBe(0);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("denied")]
    [InlineData("canceled")]
    public async Task Should_FailStartupWithoutSavingPreference_When_SetupFails(string partition)
    {
        var rig = new Rig();
        if (partition == "offline") rig.Source.OpenAsync(Arg.Any<CancellationToken>()).Returns<Stream>(_ => throw new HttpRequestException("offline"));
        else rig.Host.RegisterAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(
            partition == "denied" ? new UnauthorizedAccessException() : new OperationCanceledException()));
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Source.Received(1).OpenAsync(Arg.Any<CancellationToken>());
        rig.Messages.ShouldContain(message => message.Contains("Interactive startup stopped", StringComparison.Ordinal));
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_PropagateMainCancellation_When_DownloadIsInterrupted()
    {
        using var cancellation = new CancellationTokenSource();
        var rig = new Rig();
        rig.Source.OpenAsync(Arg.Any<CancellationToken>()).Returns<Stream>(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Should.ThrowAsync<OperationCanceledException>(() => rig.Setup.ConfigureAsync("auto", cancellation.Token));
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_CancelBeforeSideEffects_When_MainTokenIsCanceled()
    {
        var rig = new Rig();
        await Should.ThrowAsync<OperationCanceledException>(() => rig.Setup.ConfigureAsync("auto", new CancellationToken(true)));
        rig.Preferences.DidNotReceive().Read();
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("v1:ascii", "auto")]
    [InlineData("v1:nerd", "nerd")]
    public async Task Should_ReuseLegacyInstalledFontWithoutConfirmation_When_OldPreferenceIsIgnored(string? saved, string icons)
    {
        var rig = new Rig { Saved = saved };
        rig.Host.IsInstalledAsync(Arg.Any<CancellationToken>()).Returns(true);
        rig.Interaction.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await rig.Setup.ConfigureAsync(icons, Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe("v1:nerd");
        rig.Preferences.Received(1).Write("v1:nerd");
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().OfferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_AllowLaterStartupRetryWithoutOptOutWrite_When_UserDeclinesSetup()
    {
        var rig = new Rig();
        rig.Interaction.OfferAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Interaction.Received(2).OfferAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
        rig.Interaction.OfferAsync(Arg.Any<CancellationToken>()).Returns(true);
        (await rig.Setup.ConfigureAsync("nerd", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe("v1:nerd");
    }

    [Fact]
    public async Task Should_KeepInstalledLegacyFontWithoutRepeatPrompts_When_ExplicitNerdIsRequested()
    {
        var rig = new Rig { Saved = "v1:nerd" };
        rig.Host.IsInstalledAsync(Arg.Any<CancellationToken>()).Returns(true);
        rig.Interaction.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await rig.Setup.ConfigureAsync("nerd", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe("v1:nerd");
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().OfferAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("ASCII")]
    [InlineData("arbitrary-url")]
    [InlineData("")]
    public async Task Should_RejectAsciiWithoutSideEffects_When_AsciiIsSelected(string icons)
    {
        var rig = new Rig { Saved = "v1:nerd" };
        var exception = await Should.ThrowAsync<ArgumentException>(() => rig.Setup.ConfigureAsync(icons, Cancellation));
        exception.ParamName.ShouldBe("icons");
        rig.Saved.ShouldBe("v1:nerd");
        rig.Preferences.DidNotReceive().Read();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Host.DidNotReceive().IsInstalledAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_RecheckAvailabilityAndInstallOnlyMissingFont_When_ChoiceWasConfirmed(bool installed)
    {
        var rig = new Rig { Saved = "v1:nerd" };
        rig.Host.IsInstalledAsync(Arg.Any<CancellationToken>()).Returns(installed);
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe("v1:nerd");
        await rig.Source.Received(installed ? 0 : 1).OpenAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.Received(installed ? 0 : 1).OfferAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("v1:ascii")]
    [InlineData("corrupt")]
    [InlineData("v2:nerd")]
    [InlineData("")]
    public async Task Should_InstallMissingLegacyFont_When_OldPreferenceIsNotConfirmed(string state)
    {
        var rig = new Rig { Saved = state };
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe("v1:nerd");
        await rig.Host.Received(1).IsInstalledAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.Received(1).OfferAsync(Arg.Any<CancellationToken>());
        await rig.Source.Received(1).OpenAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_SkipAllSetup_When_HostIsRemote()
    {
        var rig = new Rig();
        rig.Host.IsLocal.Returns(false);
        (await rig.Setup.ConfigureAsync("nerd", Cancellation)).ShouldBeFalse();
        rig.Preferences.DidNotReceive().Read();
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().IsInstalledAsync(Arg.Any<CancellationToken>());
        rig.Messages.Single().ShouldContain("terminal host");
    }

    [Fact]
    public async Task Should_FailWithoutInstallation_When_InstalledFontPreferenceCannotBeSaved()
    {
        var rig = new Rig(writable: false);
        rig.Host.IsInstalledAsync(Arg.Any<CancellationToken>()).Returns(true);
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Should_ListAllOfficialPacksAndAliases_When_ParsingReleaseMetadata()
    {
        var catalog = FontCatalog.Parse(CatalogMetadata());
        catalog.Count.ShouldBe(72);
        catalog.Select(pack => pack.Archive).Order().ShouldBe(FontCatalog.Aliases.Keys.Order());
        var annotation = catalog.Single(pack => pack.Archive == "AnnotationMono.zip");
        annotation.DisplayName.ShouldBe("AnnotationM");
        annotation.Matches("annotationmono.zip").ShouldBeTrue();
        annotation.Matches("ANNOTATIONM").ShouldBeTrue();
        annotation.Url.ShouldBe("https://github.com/ryanoasis/nerd-fonts/releases/download/v3.5.1/AnnotationMono.zip");
        catalog.Single(pack => pack.Archive == "NerdFontsSymbolsOnly.zip").IsTextFamily.ShouldBeFalse();
        catalog.Single(pack => pack.Archive == "SourceCodePro.zip").DisplayName.ShouldBe("SauceCodePro");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("draft")]
    [InlineData("url")]
    [InlineData("path")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void Should_RejectUntrustedCatalog_When_ReleaseMetadataViolatesContract(string partition)
    {
        Should.Throw<InvalidDataException>(() => FontCatalog.Parse(CatalogMetadata(partition)));
    }

    [Fact]
    public async Task Should_RegisterSelectedArchiveAndNotices_When_CatalogSetupIsApproved()
    {
        var rig = new CatalogRig();
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm("AnnotationM NFM"));
        await rig.Source.Received(1).OpenAsync(rig.Pack, Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<CancellationToken>());
        await rig.Host.Received(1).RegisterAsync(Arg.Is<FontInstallation>(font => font.Family == "AnnotationM NFM" &&
            font.Payload.SequenceEqual(TerminalFontPlatformTests.CatalogFontFixture()) &&
            Encoding.UTF8.GetString(font.Notices["archive/LICENSE"]) == "Synthetic selected font license" &&
            Encoding.UTF8.GetString(font.Notices["glyphs/LICENSE-CODICONS"]) == "Synthetic glyph license"), Arg.Any<CancellationToken>());
        rig.Messages.ShouldContain(message => message.Contains("Select AnnotationM NFM", StringComparison.Ordinal));
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("v1:nerd", "JetBrainsMono Nerd Font Mono")]
    [InlineData("v2", "AnnotationM NFM")]
    public async Task Should_ReuseSavedFamilyOfflineWithoutNetwork_When_AutoConfirmationIsLocallyValid(string state, string family)
    {
        var rig = new CatalogRig { Saved = state == "v2" ? FontPreferences.Confirm(family) : state };
        rig.Host.FindInstalledAsync(family, null, Arg.Any<CancellationToken>()).Returns(family);
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm(family));
        await rig.Source.DidNotReceive().CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().FindInstalledAsync(null, null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("nerd")]
    public async Task Should_ReuseSelectedInstalledFamilyWithoutConfirmation_When_PickerSelectsIt(string icons)
    {
        var rig = new CatalogRig { Saved = FontPreferences.Confirm("JetBrainsMono Nerd Font Mono") };
        rig.Host.FindInstalledAsync(null, rig.Pack, Arg.Any<CancellationToken>()).Returns("AnnotationM NFM");
        rig.Interaction.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await rig.Setup.ConfigureAsync(icons, Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm("AnnotationM NFM"));
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.Received(1).PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().NoticesAsync(Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("opt-out")]
    [InlineData("corrupt")]
    [InlineData("missing-saved")]
    public async Task Should_InstallMissingCatalogFont_When_PreviousStateCannotSupplyInstalledFamily(string partition)
    {
        var rig = new CatalogRig { Saved = partition switch { "opt-out" => "v1:ascii", "corrupt" => "v2:nerd:!", _ => FontPreferences.Confirm("AnnotationM NFM") } };
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm("AnnotationM NFM"));
        await rig.Source.Received(1).CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.Received(1).OpenAsync(rig.Pack, Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("remote")]
    public async Task Should_AvoidCatalogAndDownloads_When_SetupIsNotAdmitted(string partition)
    {
        var rig = new CatalogRig();
        if (partition == "remote") rig.Host.IsLocal.Returns(false);
        if (partition == "ascii") await Should.ThrowAsync<ArgumentException>(() => rig.Setup.ConfigureAsync("ascii", Cancellation));
        else (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Source.DidNotReceive().CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("symbols")]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("size")]
    public async Task Should_CancelStartupWithoutFontDownload_When_SelectionIsUnavailableOrDeclined(string partition)
    {
        var rig = new CatalogRig();
        rig.Interaction.PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>()).Returns(partition switch
        {
            "digest" => rig.Pack with { Digest = null },
            "symbols" => rig.Pack with { Archive = "NerdFontsSymbolsOnly.zip" },
            "cancel" => null,
            "size" => rig.Pack with { Size = FontCatalog.MaximumArchiveBytes + 1 },
            _ => rig.Pack
        });
        if (partition == "decline") rig.Interaction.OfferAsync(rig.Pack, Arg.Any<CancellationToken>()).Returns(false);
        (await rig.Setup.ConfigureAsync("nerd", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("canceled")]
    [InlineData("integrity")]
    [InlineData("notices")]
    [InlineData("denied")]
    public async Task Should_FailWithoutSavingPreferenceOrBlindRetry_When_CatalogInstallationFails(string partition)
    {
        var rig = new CatalogRig();
        if (partition == "offline") rig.Source.CatalogAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<FontPack>>(_ => throw new HttpRequestException());
        if (partition == "canceled") rig.Source.OpenAsync(rig.Pack, Arg.Any<CancellationToken>()).Returns<Stream>(_ => throw new OperationCanceledException());
        if (partition == "integrity") rig.Source.OpenAsync(rig.Pack, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[rig.Archive.Length]));
        if (partition == "notices") rig.Source.NoticesAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyDictionary<string, byte[]>>(_ => throw new InvalidDataException());
        if (partition == "denied") rig.Host.RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(new UnauthorizedAccessException()));
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Source.Received(1).CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        if (partition != "denied") await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_ReloadCatalogAfterExplicitRetry_When_FirstRequestIsOffline()
    {
        var rig = new CatalogRig();
        var requests = 0;
        rig.Source.CatalogAsync(Arg.Any<CancellationToken>()).Returns(_ => ++requests == 1 ?
            Task.FromException<IReadOnlyList<FontPack>>(new HttpRequestException()) : Task.FromResult<IReadOnlyList<FontPack>>([rig.Pack]));
        rig.Interaction.RetryCatalogAsync(Arg.Any<CancellationToken>()).Returns(true);
        (await rig.Setup.ConfigureAsync("nerd", Cancellation)).ShouldBeTrue();
        requests.ShouldBe(2);
        rig.Saved.ShouldBe(FontPreferences.Confirm("AnnotationM NFM"));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("digest")]
    public async Task Should_RejectArchiveIntegrityBeforeExtraction_When_PublishedDigestDoesNotVerify(string partition)
    {
        var rig = new CatalogRig();
        var bytes = partition switch { "short" => rig.Archive[..^1], "long" => rig.Archive.Concat(new byte[] { 1 }).ToArray(), "hash" => new byte[rig.Archive.Length], _ => rig.Archive };
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        await Should.ThrowAsync<InvalidDataException>(() => FontCatalog.VerifyArchiveAsync(partition == "digest" ? rig.Pack with { Digest = null } : rig.Pack, input, output, Cancellation));
        if (partition == "digest") input.Position.ShouldBe(0);
        else input.Position.ShouldBe(bytes.Length);
    }

    [Theory]
    [InlineData("../font.ttf")]
    [InlineData("/font.ttf")]
    [InlineData("folder/../../font.ttf")]
    [InlineData("folder\\font.ttf")]
    [InlineData("C:/font.ttf")]
    [InlineData("font.ttf:stream")]
    [InlineData("AUX/LICENSE")]
    [InlineData("CON.ttf")]
    [InlineData("folder/notice.")]
    [InlineData("folder/notice ")]
    [InlineData("folder/notice?.txt")]
    public async Task Should_RejectUnsafeZipPaths_When_ExtractingSelectedArchive(string name)
    {
        using var archive = new MemoryStream(Zip([(name, new byte[] { 1 })]));
        await Should.ThrowAsync<InvalidDataException>(() => FontArchive.ExtractAsync(new CatalogRig().Pack, archive, Cancellation));
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("bomb")]
    [InlineData("size")]
    [InlineData("entries")]
    [InlineData("malformed")]
    [InlineData("family")]
    [InlineData("license")]
    [InlineData("duplicate")]
    [InlineData("notice-limit")]
    public async Task Should_RejectUnsafeOrUnusableZip_When_ValidatingArchiveContents(string partition)
    {
        var entries = new List<(string, byte[])> { ("face.ttf", partition == "malformed" ? new byte[64] : TerminalFontPlatformTests.CatalogFontFixture(partition == "family" ? "FiraCode Nerd Font Mono" : "AnnotationM NFM")) };
        if (partition != "license") entries.Add(("LICENSE", Encoding.UTF8.GetBytes("Synthetic selected font license")));
        if (partition == "bomb") entries.Add(("bomb.txt", new byte[1024 * 1024]));
        if (partition == "size") entries.Add(("large.bin", new byte[FontArchive.MaximumFaceBytes + 1]));
        if (partition == "entries") entries.AddRange(Enumerable.Range(0, 4096).Select(index => ("empty" + index, Array.Empty<byte>())));
        if (partition == "duplicate") entries.Add(("FACE.ttf", TerminalFontPlatformTests.CatalogFontFixture()));
        if (partition == "notice-limit")
        {
            var bytes = new byte[1024 * 1024];
            new Random(33).NextBytes(bytes);
            entries.AddRange(Enumerable.Range(0, 9).Select(index => ("notice" + index + ".txt", bytes)));
        }
        using var archive = new MemoryStream(Zip(entries, partition == "symlink"));
        await Should.ThrowAsync<InvalidDataException>(() => FontArchive.ExtractAsync(new CatalogRig().Pack, archive, Cancellation));
    }

    [Fact]
    public async Task Should_RetainStandaloneOflBytes_When_ArchiveSuppliesSelectedFontLicense()
    {
        var license = Encoding.UTF8.GetBytes("Synthetic SIL Open Font License");
        using var archive = new MemoryStream(Zip([("face.ttf", TerminalFontPlatformTests.CatalogFontFixture()), ("OFL", license)]));
        var installation = await FontArchive.ExtractAsync(new CatalogRig().Pack, archive, Cancellation);
        installation.Family.ShouldBe("AnnotationM NFM");
        installation.Notices["archive/OFL"].ShouldBe(license);
    }

    internal static byte[] Zip(IEnumerable<(string Name, byte[] Bytes)> entries, bool symlink = false)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
                if (symlink) entry.ExternalAttributes = 0xa000 << 16;
                using var stream = entry.Open();
                stream.Write(item.Bytes);
            }
        return output.ToArray();
    }

    private static byte[] CatalogMetadata(string partition = "valid") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        tag_name = partition == "version" ? "v0.0.0" : FontCatalog.Version,
        draft = partition == "draft", prerelease = false,
        assets = (partition == "empty" ? [] : ExpectedArchives.Split('|').Select(name => name + ".zip").Concat(partition == "duplicate" ? new[] { "AnnotationMono.zip" } : []))
            .Select(name => new { name = partition == "path" ? "../bad.zip" : name, size = 62992429L, state = "uploaded",
                browser_download_url = partition == "url" ? "https://example.invalid/" + name : FontCatalog.ReleaseRoot + name,
                digest = "sha256:cde432bc5c30723f2431ac655964ad8e4c09877a5fcd176c4ddc38ed71c79ffd" }).ToArray()
    });

    private const string ExpectedArchives = "0xProto|3270|AdwaitaMono|Agave|AnnotationMono|AnonymousPro|Arimo|AtkinsonHyperlegibleMono|AurulentSansMono|BigBlueTerminal|BitstreamVeraSansMono|IBMPlexMono|CascadiaCode|CascadiaMono|CodeNewRoman|ComicShannsMono|CommitMono|Cousine|D2Coding|DaddyTimeMono|DejaVuSansMono|DepartureMono|DroidSansMono|EnvyCodeR|FantasqueSansMono|FiraCode|FiraMono|GeistMono|Go-Mono|Gohu|GoogleSansCode|Hack|Hasklig|HeavyData|Hermit|iA-Writer|Inconsolata|InconsolataGo|InconsolataLGC|IntelOneMono|Iosevka|IosevkaTerm|IosevkaTermSlab|JetBrainsMono|Lekton|LiberationMono|Lilex|MartianMono|Meslo|Monaspace|Monofur|Monoid|Mononoki|MPlus|Noto|OpenDyslexic|Overpass|ProFont|ProggyClean|Recursive|RobotoMono|ShareTechMono|SourceCodePro|SpaceMono|NerdFontsSymbolsOnly|Terminus|Tinos|Ubuntu|UbuntuMono|UbuntuSans|VictorMono|ZedMono";

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("v1:ascii", "auto")]
    [InlineData(null, "nerd")]
    [InlineData("v1:ascii", "nerd")]
    public async Task Should_ReuseAnyInstalledFamilyOffline_When_PreferenceIsUnsavedOrLegacyAscii(string? saved, string icons)
    {
        var rig = new CatalogRig { Saved = saved };
        rig.Host.FindInstalledAsync(null, null, Arg.Any<CancellationToken>()).Returns("FiraCode Nerd Font Mono");
        (await rig.Setup.ConfigureAsync(icons, Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm("FiraCode Nerd Font Mono"));
        rig.Preferences.Received(1).Write(FontPreferences.Confirm("FiraCode Nerd Font Mono"));
        await rig.Host.Received(1).FindInstalledAsync(null, null, Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().NoticesAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().OfferAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("nerd")]
    public async Task Should_ChooseAnotherInstalledFamilyOffline_When_SavedFamilyWasRemoved(string icons)
    {
        var rig = new CatalogRig { Saved = FontPreferences.Confirm("AnnotationM NFM") };
        rig.Host.FindInstalledAsync(null, null, Arg.Any<CancellationToken>()).Returns("FiraCode Nerd Font Mono");
        (await rig.Setup.ConfigureAsync(icons, Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm("FiraCode Nerd Font Mono"));
        rig.Host.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IFontHost.FindInstalledAsync))
            .Select(call => call.GetArguments()[0]).ShouldBe(new object?[] { "AnnotationM NFM", null });
        await rig.Source.DidNotReceive().CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("nerd")]
    public async Task Should_SkipRepeatPromptsAfterInstallation_When_NextStartupFindsActualFamily(string icons)
    {
        var rig = new CatalogRig();
        (await rig.Setup.ConfigureAsync(icons, Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe(FontPreferences.Confirm("AnnotationM NFM"));
        rig.Host.FindInstalledAsync("AnnotationM NFM", null, Arg.Any<CancellationToken>()).Returns("AnnotationM NFM");
        (await rig.Setup.ConfigureAsync(icons, Cancellation)).ShouldBeTrue();
        await rig.Source.Received(1).CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.Received(1).OpenAsync(rig.Pack, Arg.Any<CancellationToken>());
        await rig.Host.Received(1).RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
        await rig.Interaction.Received(1).PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>());
        await rig.Interaction.Received(1).OfferAsync(rig.Pack, Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        rig.Preferences.Received(2).Write(FontPreferences.Confirm("AnnotationM NFM"));
    }

    [Fact]
    public async Task Should_SkipLegacyRepeatPromptsAfterInstallation_When_NextStartupFindsFont()
    {
        var rig = new Rig { Saved = "v1:ascii" };
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        rig.Saved.ShouldBe("v1:nerd");
        rig.Host.IsInstalledAsync(Arg.Any<CancellationToken>()).Returns(true);
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeTrue();
        await rig.Source.Received(1).OpenAsync(Arg.Any<CancellationToken>());
        await rig.Host.Received(1).RegisterAsync(Arg.Is<byte[]>(bytes => bytes.SequenceEqual(Bytes)), Arg.Any<CancellationToken>());
        await rig.Interaction.Received(1).OfferAsync(Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
        rig.Preferences.Received(2).Write("v1:nerd");
        rig.Preferences.DidNotReceive().Write("v1:ascii");
    }

    [Theory]
    [InlineData("v1:ascii")]
    [InlineData("v2")]
    public async Task Should_PreservePreviousPreferenceWithoutWritingAscii_When_MissingCatalogFontSetupIsCanceled(string state)
    {
        var saved = state == "v2" ? FontPreferences.Confirm("FiraCode Nerd Font Mono") : state;
        var rig = new CatalogRig { Saved = saved };
        rig.Interaction.PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>()).Returns((FontPack?)null);
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBe(saved);
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
        await rig.Interaction.DidNotReceive().ConfirmAsync(Arg.Any<CancellationToken>());
    }

    private sealed class CatalogRig
    {
        public IFontHost Host { get; } = Substitute.For<IFontHost>();
        public IFontPreferences Preferences { get; } = Substitute.For<IFontPreferences>();
        public IFontSource Source { get; } = Substitute.For<IFontSource>();
        public IFontInteraction Interaction { get; } = Substitute.For<IFontInteraction>();
        public byte[] Archive { get; } = Zip([("face.ttf", TerminalFontPlatformTests.CatalogFontFixture()), ("LICENSE", Encoding.UTF8.GetBytes("Synthetic selected font license"))]);
        public FontPack Pack { get; }
        public string? Saved { get; set; }
        public List<string> Messages { get; } = [];
        public TerminalFontSetup Setup { get; }
        public CatalogRig(TimeProvider? time = null, bool writable = true)
        {
            Pack = new("AnnotationMono.zip", "AnnotationM", Archive.Length, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Archive)));
            Host.IsLocal.Returns(true);
            Host.FindInstalledAsync(Arg.Any<string?>(), Arg.Any<FontPack?>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            Preferences.Read().Returns(_ => Saved);
            Preferences.When(value => value.Write(Arg.Any<string>())).Do(call =>
            {
                if (!writable) throw new UnauthorizedAccessException();
                Saved = call.Arg<string>();
            });
            Source.CatalogAsync(Arg.Any<CancellationToken>()).Returns(new FontPack[] { Pack });
            Source.OpenAsync(Pack, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Archive));
            Source.NoticesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, byte[]> { ["LICENSE-CODICONS"] = Encoding.UTF8.GetBytes("Synthetic glyph license") });
            Interaction.PickAsync(Arg.Any<IReadOnlyList<FontPack>>(), Arg.Any<CancellationToken>()).Returns(Pack);
            Interaction.OfferAsync(Pack, Arg.Any<CancellationToken>()).Returns(true);
            Interaction.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(true);
            Interaction.RunAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
            Interaction.When(value => value.Report(Arg.Any<string>())).Do(call => Messages.Add(call.Arg<string>()));
            Setup = new(Host, Preferences, Source, Interaction, null, time ?? TimeProvider.System);
        }
    }

    [Theory]
    [InlineData("catalog", 45)]
    [InlineData("archive", 300)]
    public async Task Should_CancelBoundedCatalogOperation_When_DeadlineElapses(string stage, int seconds)
    {
        var clock = new ManualTimeProvider();
        var rig = new CatalogRig(clock);
        var observed = CancellationToken.None;
        if (stage == "catalog") rig.Source.CatalogAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observed = call.Arg<CancellationToken>();
            await Task.Delay(Timeout.Infinite, observed);
            return (IReadOnlyList<FontPack>)Array.Empty<FontPack>();
        });
        else rig.Source.OpenAsync(rig.Pack, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observed = call.Arg<CancellationToken>();
            await Task.Delay(Timeout.Infinite, observed);
            return Stream.Null;
        });
        var running = rig.Setup.ConfigureAsync("nerd", Cancellation);
        running.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        observed.IsCancellationRequested.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        (await running).ShouldBeFalse();
        observed.IsCancellationRequested.ShouldBeTrue();
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_PropagateChatCancellationWithoutRegistration_When_CatalogOperationIsInterrupted()
    {
        var rig = new CatalogRig();
        using var cancellation = new CancellationTokenSource();
        rig.Source.CatalogAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<FontPack>>(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        await Should.ThrowAsync<OperationCanceledException>(() => rig.Setup.ConfigureAsync("nerd", cancellation.Token));
        rig.Saved.ShouldBeNull();
        rig.Preferences.DidNotReceive().Write(Arg.Any<string>());
        await rig.Host.DidNotReceive().RegisterAsync(Arg.Any<FontInstallation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_FailBeforeNetwork_When_InstalledCatalogPreferenceWriteIsDenied()
    {
        var rig = new CatalogRig(writable: false);
        rig.Host.FindInstalledAsync(null, null, Arg.Any<CancellationToken>()).Returns("AnnotationM NFM");
        (await rig.Setup.ConfigureAsync("auto", Cancellation)).ShouldBeFalse();
        rig.Saved.ShouldBeNull();
        await rig.Source.DidNotReceive().CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.DidNotReceive().OpenAsync(Arg.Any<FontPack>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_WireCatalogCoordinatorThroughRuntimeServices_When_FontSetupIsResolved()
    {
        var rig = new CatalogRig();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, rig.Host);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, rig.Preferences);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, rig.Source);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, rig.Interaction);
        Microsoft.Extensions.DependencyInjection.CliServiceCollectionExtensions.AddCodeAssistCli(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var setup = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ITerminalFontSetup>(provider);
        (await setup.ConfigureAsync("nerd", Cancellation)).ShouldBeTrue();
        await rig.Source.Received(1).CatalogAsync(Arg.Any<CancellationToken>());
        await rig.Source.Received(1).OpenAsync(rig.Pack, Arg.Any<CancellationToken>());
        rig.Saved.ShouldBe(FontPreferences.Confirm("AnnotationM NFM"));
    }

    private sealed class Rig
    {
        public IFontHost Host { get; } = Substitute.For<IFontHost>();
        public IFontPreferences Preferences { get; } = Substitute.For<IFontPreferences>();
        public IFontSource Source { get; } = Substitute.For<IFontSource>();
        public IFontInteraction Interaction { get; } = Substitute.For<IFontInteraction>();
        public string? Saved { get; set; }
        public TrackingStream Stream { get; }
        public List<string> Messages { get; } = [];
        public TerminalFontSetup Setup { get; }

        public Rig(byte[]? bytes = null, TimeProvider? time = null, bool writable = true)
        {
            Stream = new TrackingStream(bytes ?? Bytes);
            Host.IsLocal.Returns(true);
            Preferences.Read().Returns(_ => Saved);
            Preferences.When(preferences => preferences.Write(Arg.Any<string>())).Do(call =>
            {
                if (!writable) throw new UnauthorizedAccessException();
                Saved = call.Arg<string>();
            });
            Source.OpenAsync(Arg.Any<CancellationToken>()).Returns(Stream);
            Interaction.OfferAsync(Arg.Any<CancellationToken>()).Returns(true);
            Interaction.ConfirmAsync(Arg.Any<CancellationToken>()).Returns(true);
            Interaction.RunAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
            Interaction.When(interaction => interaction.Report(Arg.Any<string>())).Do(call => Messages.Add(call.Arg<string>()));
            Setup = new TerminalFontSetup(Host, Preferences, Source, Interaction, TestPin, time ?? TimeProvider.System);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _elapsed + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            _elapsed += duration;
            foreach (var timer in _timers.Where(timer => !timer.Disposed && timer.Due <= _elapsed).ToArray()) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due { get; private set; } = due;
            public bool Disposed { get; private set; }
            public void Fire()
            {
                Disposed = true;
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}