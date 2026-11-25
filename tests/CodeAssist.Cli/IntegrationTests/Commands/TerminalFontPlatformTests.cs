using System.Buffers.Binary;
using System.Text;
using CodeAssist.Cli.Fonts;
using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;

namespace CodeAssist.Cli.IntegrationTests.Commands;

public sealed class TerminalFontPlatformTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly byte[] Bytes = [1, 2, 3, 4];
    private static readonly FontPin TestPin = new(4, "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a");

    [Theory]
    [InlineData("offer")]
    [InlineData("confirm")]
    public async Task Should_PropagateMainCancellation_When_SelectionIsInterrupted(string stage)
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        await Should.ThrowAsync<OperationCanceledException>(() => stage == "offer" ? interaction.OfferAsync(cancellation.Token) : interaction.ConfirmAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(20, true)]
    [InlineData(80, false)]
    [InlineData(80, true)]
    public async Task Should_DefaultToExitStartupAndRenderFullSample_When_ConfirmingAtSupportedWidths(int width, bool confirm)
    {
        using var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Interactive = true;
        if (confirm) console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        (await interaction.ConfirmAsync(Cancellation)).ShouldBe(confirm);
        console.Output.ShouldNotContain("Use ASCII");
        foreach (var glyph in new[] { "\ueb08", "\uea76", "\uea60", "\uec0f", "\uf15c", "\uf044", "\uf489", "\uf07b" })
            console.Output.ShouldContain(glyph);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_CancelSetupAndReleaseInput_When_EscapeOrControlCIsPressed(bool exitChat)
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(new ConsoleKeyInfo(exitChat ? 'c' : '\u001b', exitChat ? ConsoleKey.C : ConsoleKey.Escape, false, false, exitChat));
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        var cleaned = false;
        async Task Operation(CancellationToken cancellation)
        {
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            finally { cleaned = true; }
        }
        if (exitChat) await Should.ThrowAsync<FontSetupExitException>(() => interaction.RunAsync(Operation, Cancellation));
        else await Should.ThrowAsync<OperationCanceledException>(() => interaction.RunAsync(Operation, Cancellation));
        cleaned.ShouldBeTrue();
        console.Input.PushKey(ConsoleKey.X);
        (await console.Input.ReadKeyAsync(true, Cancellation))!.Value.Key.ShouldBe(ConsoleKey.X);
    }

    [Theory]
    [InlineData("SSH_CONNECTION")]
    [InlineData("SSH_CLIENT")]
    [InlineData("SSH_TTY")]
    [InlineData("WSL_DISTRO_NAME")]
    [InlineData("WSL_INTEROP")]
    [InlineData("container")]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER")]
    [InlineData("DOTNET_RUNNING_IN_CONTAINERS")]
    [InlineData("CI")]
    public void Should_SkipHostSetup_When_RemoteOrContainerMarkerIsPresent(string marker)
    {
        FontHost.IsLocalEnvironment(new Dictionary<string, string> { [marker] = "1" }, false, true, false).ShouldBeFalse();
    }

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    public void Should_RequireLocalInteractiveHost_When_CheckingEnvironment(bool redirected, bool interactive, bool containerFile, bool expected)
    {
        FontHost.IsLocalEnvironment(new Dictionary<string, string>(), redirected, interactive, containerFile).ShouldBe(expected);
        FontHost.IsLocalEnvironment(new Dictionary<string, string> { ["TERM"] = "dumb" }, false, true, false).ShouldBeFalse();
        FontHost.IsLocalEnvironment(new Dictionary<string, string> { ["CI"] = "TRUE" }, false, true, false).ShouldBeFalse();
        FontHost.IsLocalEnvironment(new Dictionary<string, string> { ["CI"] = "false" }, false, true, false).ShouldBeTrue();
    }

    [Fact]
    public void Should_ChooseUserDirectories_When_SelectingPlatformTargets()
    {
        var home = Path.GetFullPath("synthetic-home");
        var local = Path.Combine(home, "local");
        var xdg = Path.Combine(home, "data");
        FontPaths.For(FontPlatform.Windows, home, local).TargetDirectory.ShouldBe(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
        FontPaths.For(FontPlatform.MacOS, home, local).TargetDirectory.ShouldBe(Path.Combine(home, "Library", "Fonts"));
        FontPaths.For(FontPlatform.Linux, home, local, xdg).TargetDirectory.ShouldBe(Path.Combine(xdg, "fonts", "codeassist"));
        FontPaths.For(FontPlatform.Linux, home, local, "relative").TargetDirectory.ShouldBe(Path.Combine(home, ".local", "share", "fonts", "codeassist"));
        FontPaths.For(FontPlatform.Unsupported, home, local).SearchDirectories.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("confirmed", "v1:nerd")]
    [InlineData("opted-out", "v1:ascii")]
    [InlineData("unknown", "v1:ascii")]
    [InlineData("oversized", "v1:ascii")]
    [InlineData("empty", "v1:ascii")]
    public void Should_ReadOnlyBoundedLiteralState_When_PreferencesAreLoaded(string partition, string expected)
    {
        var files = Substitute.For<IFontFiles>();
        var preferences = new FontPreferences(files, "synthetic-state", "terminal-one");
        files.Exists(preferences.PathName).Returns(true);
        using var stream = new ObservedStream(Encoding.ASCII.GetBytes(partition switch
        {
            "confirmed" => "v1:nerd",
            "opted-out" => "v1:ascii",
            "unknown" => "{\"icons\":\"nerd\"}",
            "oversized" => new string('x', 10000),
            _ => ""
        }));
        files.OpenRead(preferences.PathName).Returns(stream);
        preferences.Read().ShouldBe(expected);
        if (partition == "oversized") stream.Consumed.ShouldBe(513);
        files.DidNotReceive().WriteAtomic(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    [Fact]
    public void Should_PersistExactChoiceAndSeparateTerminals_When_SavingPreferences()
    {
        var files = Substitute.For<IFontFiles>();
        var first = new FontPreferences(files, "synthetic-state", "terminal-one");
        var second = new FontPreferences(files, "synthetic-state", "terminal-two");
        first.PathName.ShouldNotBe(second.PathName);
        first.Read().ShouldBeNull();
        first.Write("v1:nerd");
        files.Received(1).WriteAtomic(first.PathName, Arg.Is<byte[]>(bytes => Encoding.ASCII.GetString(bytes) == "v1:nerd"));
        Should.Throw<ArgumentException>(() => first.Write("../untrusted"));
        files.Received(1).WriteAtomic(Arg.Any<string>(), Arg.Any<byte[]>());
        var persisted = Encoding.ASCII.GetBytes("v1:nerd");
        files.Exists(first.PathName).Returns(true);
        files.OpenRead(first.PathName).Returns(_ => new MemoryStream(persisted));
        new FontPreferences(files, "synthetic-state", "terminal-one").Read().ShouldBe("v1:nerd");
    }

    [Theory]
    [InlineData(FontPlatform.Windows)]
    [InlineData(FontPlatform.MacOS)]
    [InlineData(FontPlatform.Linux)]
    public async Task Should_WriteVerifiedPayloadWithoutOverwrite_When_RegisteringForUser(FontPlatform platform)
    {
        var files = Substitute.For<IFontFiles>();
        var host = Host(files, platform);
        await host.RegisterAsync(Bytes, Cancellation);
        files.Received(1).WriteNew(Arg.Is<string>(path => path.StartsWith(Path.Combine("synthetic-fonts", ".codeassist-font-"))),
            Arg.Is<byte[]>(bytes => bytes.SequenceEqual(Bytes)));
        files.Received(1).MoveNew(Arg.Any<string>(), Path.Combine("synthetic-fonts", FontPaths.FileName));
        await files.Received(1).RefreshAsync(platform, Path.Combine("synthetic-fonts", FontPaths.FileName), Cancellation);
        files.DidNotReceive().Delete(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_DeleteOnlyNewDestination_When_RegistrationFails(bool preexisting)
    {
        var files = Substitute.For<IFontFiles>();
        var destination = Path.Combine("synthetic-fonts", FontPaths.FileName);
        files.Exists(destination).Returns(preexisting);
        files.OpenRead(destination).Returns(_ => new MemoryStream(Bytes));
        files.RefreshAsync(Arg.Any<FontPlatform>(), destination, Cancellation).Returns(_ => Task.FromException(new UnauthorizedAccessException()));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => Host(files).RegisterAsync(Bytes, Cancellation));
        if (preexisting)
        {
            files.DidNotReceive().Delete(Arg.Any<string>());
            files.DidNotReceive().WriteNew(Arg.Any<string>(), Arg.Any<byte[]>());
        }
        else files.Received(1).Delete(destination);
    }

    [Fact]
    public async Task Should_LeavePreexistingBytesUntouched_When_OwnedNameHasDifferentPayload()
    {
        var files = Substitute.For<IFontFiles>();
        var destination = Path.Combine("synthetic-fonts", FontPaths.FileName);
        files.Exists(destination).Returns(true);
        files.OpenRead(destination).Returns(_ => new MemoryStream(new byte[] { 4, 3, 2, 1 }));
        await Should.ThrowAsync<InvalidDataException>(() => Host(files).RegisterAsync(Bytes, Cancellation));
        files.DidNotReceive().WriteNew(Arg.Any<string>(), Arg.Any<byte[]>());
        files.DidNotReceive().Delete(Arg.Any<string>());
        await files.DidNotReceive().RefreshAsync(Arg.Any<FontPlatform>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_RejectUnverifiedPayloadAtHostBoundary_When_RegistrationIsCalledDirectly()
    {
        var files = Substitute.For<IFontFiles>();
        await Should.ThrowAsync<InvalidDataException>(() => Host(files).RegisterAsync([4, 3, 2, 1], Cancellation));
        files.DidNotReceive().CreateDirectory(Arg.Any<string>());
        await files.DidNotReceive().RefreshAsync(Arg.Any<FontPlatform>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("move")]
    [InlineData("cancel")]
    [InlineData("refresh")]
    public async Task Should_CleanOnlyCreatedFiles_When_InstallationIsInterrupted(string partition)
    {
        var files = Substitute.For<IFontFiles>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        if (partition == "move") files.When(value => value.MoveNew(Arg.Any<string>(), Arg.Any<string>())).Do(_ => throw new IOException());
        if (partition == "cancel") files.When(value => value.WriteNew(Arg.Any<string>(), Arg.Any<byte[]>())).Do(_ => cancellation.Cancel());
        if (partition == "refresh") files.RefreshAsync(Arg.Any<FontPlatform>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(new OperationCanceledException()));
        if (partition == "move") await Should.ThrowAsync<IOException>(() => Host(files).RegisterAsync(Bytes, cancellation.Token));
        else await Should.ThrowAsync<OperationCanceledException>(() => Host(files).RegisterAsync(Bytes, cancellation.Token));
        if (partition == "refresh") files.Received(1).Delete(Path.Combine("synthetic-fonts", FontPaths.FileName));
        else files.Received(1).Delete(Arg.Is<string>(path => Path.GetFileName(path).StartsWith(".codeassist-font-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public async Task Should_ReuseCompatibleSharedFont_When_AllGlyphsAndMonospaceFamilyMatch(int format)
    {
        var files = Substitute.For<IFontFiles>();
        files.EnumerateFonts("shared-fonts").Returns(["shared-fonts/renamed.ttf"]);
        files.OpenRead("shared-fonts/renamed.ttf").Returns(_ => new MemoryStream(FontFixture(format)));
        (await Host(files).IsInstalledAsync(Cancellation)).ShouldBeTrue();
        files.DidNotReceive().WriteNew(Arg.Any<string>(), Arg.Any<byte[]>());
        files.DidNotReceive().Delete(Arg.Any<string>());
    }

    [Theory]
    [InlineData("missing-glyph")]
    [InlineData("proportional")]
    [InlineData("wrong-family")]
    [InlineData("truncated")]
    [InlineData("bad-table")]
    public void Should_RejectIncompatibleFonts_When_InspectingUntrustedTables(string partition)
    {
        var bytes = FontFixture(12, partition);
        using var stream = new MemoryStream(bytes);
        TrueTypeFont.IsCompatible(stream).ShouldBeFalse();
    }

    [Theory]
    [InlineData("missing-glyph", false)]
    [InlineData("mapped", true)]
    [InlineData("mapped-zero", false)]
    public void Should_ResolveNonzeroGlyphMappings_When_InspectingFormatFour(string partition, bool expected)
    {
        using var stream = new MemoryStream(FontFixture(4, partition));
        TrueTypeFont.IsCompatible(stream).ShouldBe(expected);
    }

    private static FontHost Host(IFontFiles files, FontPlatform platform = FontPlatform.Windows) =>
        new(files, new FontPaths(platform, "synthetic-fonts", ["shared-fonts"]), true, TestPin);

    [Theory]
    [InlineData("AnnotationM NFM")]
    [InlineData("FiraCode Nerd Font Mono")]
    public void Should_ParseActualRegularMonoFamily_When_ValidatingCatalogFace(string family)
    {
        using var stream = new MemoryStream(CatalogFontFixture(family));
        TrueTypeFont.Describe(stream, strict: true).ShouldBe(family);
    }

    [Theory]
    [InlineData("missing-text")]
    [InlineData("missing-icon")]
    [InlineData("wrong-style")]
    [InlineData("wrong-width")]
    [InlineData("bad-header")]
    [InlineData("bad-glyph-index")]
    public void Should_RejectUnsuitableCatalogFace_When_TextStyleMetricsOrGlyphsFail(string partition)
    {
        using var stream = new MemoryStream(CatalogFontFixture(partition: partition));
        TrueTypeFont.Describe(stream, strict: true).ShouldBeNull();
    }

    internal static byte[] CatalogFontFixture(string family = "AnnotationM NFM", string partition = "valid")
    {
        static void Write16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), (ushort)value);
        static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        var tables = new Dictionary<string, byte[]> { ["head"] = new byte[54], ["maxp"] = new byte[32], ["hhea"] = new byte[36],
            ["hmtx"] = new byte[12], ["post"] = new byte[32], ["glyf"] = new byte[24] };
        Write32(tables["head"], 12, partition == "bad-header" ? 0u : 0x5f0f3cf5);
        Write16(tables["maxp"], 4, 3);
        Write16(tables["hhea"], 34, 3);
        Write32(tables["post"], 12, 1);
        Write16(tables["hmtx"], 0, 600);
        Write16(tables["hmtx"], 4, 600);
        Write16(tables["hmtx"], 8, partition == "wrong-width" ? 300 : 600);
        var familyBytes = Encoding.BigEndianUnicode.GetBytes(family);
        var styleBytes = Encoding.BigEndianUnicode.GetBytes(partition == "wrong-style" ? "Bold" : "Regular");
        var names = new byte[30 + familyBytes.Length + styleBytes.Length];
        Write16(names, 2, 2);
        Write16(names, 4, 30);
        Write16(names, 6, 3);
        Write16(names, 8, 1);
        Write16(names, 12, 16);
        Write16(names, 14, familyBytes.Length);
        Write16(names, 18, 3);
        Write16(names, 20, 1);
        Write16(names, 24, 17);
        Write16(names, 26, styleBytes.Length);
        Write16(names, 28, familyBytes.Length);
        familyBytes.CopyTo(names, 30);
        styleBytes.CopyTo(names, 30 + familyBytes.Length);
        tables.Add("name", names);
        var characters = Enumerable.Range(32, 95).Concat(TrueTypeFont.RequiredGlyphs).Order().ToArray();
        var cmap = new byte[28 + characters.Length * 12];
        Write16(cmap, 2, 1);
        Write16(cmap, 4, 3);
        Write16(cmap, 6, 10);
        Write32(cmap, 8, 12);
        Write16(cmap, 12, 12);
        Write32(cmap, 16, (uint)(cmap.Length - 12));
        Write32(cmap, 24, (uint)characters.Length);
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            Write32(cmap, 28 + index * 12, (uint)character);
            Write32(cmap, 32 + index * 12, (uint)character);
            var glyph = character == 65 ? 2u : 1u;
            if (partition == "missing-text" && character == 65 || partition == "missing-icon" && character == 0xeb08) glyph = 0;
            if (partition == "bad-glyph-index" && character == 65) glyph = 3;
            Write32(cmap, 36 + index * 12, glyph);
        }
        tables.Add("cmap", cmap);
        var result = new byte[12 + tables.Count * 16 + tables.Values.Sum(bytes => bytes.Length)];
        Write32(result, 0, 0x00010000);
        Write16(result, 4, tables.Count);
        var position = 12 + tables.Count * 16;
        var tableIndex = 0;
        foreach (var table in tables)
        {
            Encoding.ASCII.GetBytes(table.Key).CopyTo(result, 12 + tableIndex * 16);
            Write32(result, 20 + tableIndex * 16, (uint)position);
            Write32(result, 24 + tableIndex * 16, (uint)table.Value.Length);
            table.Value.CopyTo(result, position);
            position += table.Value.Length;
            tableIndex++;
        }
        return result;
    }

    [Theory]
    [InlineData(20)]
    [InlineData(80)]
    public async Task Should_RecoverSearchAndSelectAlias_When_PickerHasNoMatches(int width)
    {
        using var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushText("zzzz");
        console.Input.PushKey(ConsoleKey.Enter);
        for (var index = 0; index < 4; index++) console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushText("annotationmono.zip");
        console.Input.PushKey(ConsoleKey.Enter);
        var pack = new FontPack("AnnotationMono.zip", "AnnotationM", 100, null);
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        (await interaction.PickAsync([new("FiraCode.zip", "FiraCode", 100, null), pack], Cancellation)).ShouldBe(pack);
        console.Output.ShouldContain("No matching fonts");
        console.Output.ShouldContain("1 of 2 packs");
        console.Output.ShouldNotContain("Use ASCII");
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("empty")]
    [InlineData("symbols")]
    public async Task Should_CancelStartupWithoutTextFamily_When_PickerIsCanceledEmptyOrSymbolsOnly(string partition)
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        if (partition == "symbols")
        {
            console.Input.PushKey(ConsoleKey.Enter);
        }
        if (partition == "empty") console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushKey(ConsoleKey.Escape);
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        (await interaction.PickAsync(partition == "empty" ? [] : [new("NerdFontsSymbolsOnly.zip", "Symbols", 100, null)], Cancellation)).ShouldBeNull();
        console.Output.ShouldContain(partition == "empty" ? "No matching fonts" : "symbols only");
        console.Output.ShouldNotContain("Use ASCII");
        console.Input.PushKey(ConsoleKey.X);
        (await console.Input.ReadKeyAsync(true, Cancellation))!.Value.Key.ShouldBe(ConsoleKey.X);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("end")]
    [InlineData("query-limit")]
    public async Task Should_SelectExpectedFamily_When_PickerUsesBoundedKeyboardNavigation(string partition)
    {
        using var console = new TestConsole();
        console.Profile.Width = 20;
        console.Profile.Capabilities.Interactive = true;
        if (partition == "home")
        {
            console.Input.PushKey(ConsoleKey.End);
            console.Input.PushKey(ConsoleKey.Home);
        }
        else if (partition == "end")
        {
            console.Input.PushKey(ConsoleKey.End);
            console.Input.PushKey(ConsoleKey.UpArrow);
        }
        else
        {
            console.Input.PushText(new string('a', 97));
            for (var index = 0; index < 96; index++) console.Input.PushKey(ConsoleKey.Backspace);
        }
        console.Input.PushKey(ConsoleKey.Enter);
        var expected = new FontPack("FiraCode.zip", "FiraCode", 100, null);
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        (await interaction.PickAsync([expected, new("AnnotationMono.zip", "AnnotationM", 100, null)], Cancellation)).ShouldBe(expected);
        var visible = System.Text.RegularExpressions.Regex.Replace(console.Output, "\u001b\\[[0-?]*[ -/]*[@-~]", "");
        visible.Replace("\r", "", StringComparison.Ordinal).Split('\n').All(line => line.Length <= 20).ShouldBeTrue(visible);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(80)]
    public async Task Should_SelectFirstFamilyWithoutAsciiRow_When_PickerOpens(int width)
    {
        using var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(ConsoleKey.Enter);
        var first = new FontPack("FiraCode.zip", "FiraCode", 100, null);
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        (await interaction.PickAsync([first, new("AnnotationMono.zip", "AnnotationM", 100, null)], Cancellation)).ShouldBe(first);
        console.Output.ShouldContain("> FiraCode");
        console.Output.ShouldNotContain("Use ASCII");
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("catalog")]
    [InlineData("retry")]
    public async Task Should_DefaultToExitStartupWithoutAsciiChoice_When_SetupMenuOpens(string stage)
    {
        using var console = new TestConsole();
        console.Profile.Width = 120;
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(ConsoleKey.Enter);
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        var result = stage switch
        {
            "pinned" => await interaction.OfferAsync(Cancellation),
            "catalog" => await interaction.OfferAsync(new("FiraCode.zip", "FiraCode", 100, null), Cancellation),
            _ => await interaction.RetryCatalogAsync(Cancellation)
        };
        result.ShouldBeFalse();
        console.Output.ShouldContain("Exit startup");
        console.Output.ShouldNotContain("Use ASCII");
    }

    [Fact]
    public async Task Should_ExitChatRatherThanConfirmFont_When_PickerReceivesControlC()
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(new ConsoleKeyInfo('c', ConsoleKey.C, false, false, true));
        var interaction = new SpectreFontInteraction(console, new FontPaths(FontPlatform.Windows, "synthetic-fonts", []));
        await Should.ThrowAsync<FontSetupExitException>(() => interaction.PickAsync([new("AnnotationMono.zip", "AnnotationM", 100, null)], Cancellation));
    }

    [Theory]
    [InlineData(FontPlatform.Windows)]
    [InlineData(FontPlatform.MacOS)]
    [InlineData(FontPlatform.Linux)]
    public async Task Should_PersistFontAndNoticesAtomically_When_RegisteringSelectedFamily(FontPlatform platform)
    {
        var files = Substitute.For<IFontFiles>();
        var payload = CatalogFontFixture();
        var installation = new FontInstallation("AnnotationM NFM", payload, new Dictionary<string, byte[]> { ["archive/LICENSE"] = [1, 2, 3], ["glyphs/LICENSE"] = [4, 5, 6] });
        await Host(files, platform).RegisterAsync(installation, Cancellation);
        var destination = Path.Combine("synthetic-fonts", "CodeAssist-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)) + ".ttf");
        files.Received(1).MoveNew(Arg.Any<string>(), destination);
        files.Received(1).MoveNew(Arg.Any<string>(), Path.Combine(destination + ".notices", "archive", "LICENSE"));
        files.Received(1).MoveNew(Arg.Any<string>(), Path.Combine(destination + ".notices", "glyphs", "LICENSE"));
        await files.Received(1).RefreshAsync(platform, destination, Cancellation);
        files.DidNotReceive().WriteAtomic(Arg.Any<string>(), Arg.Any<byte[]>());
        files.DidNotReceive().Delete(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreservePreexistingSelectedFontAndNotices_When_RegistrationFails(bool preexisting)
    {
        var files = Substitute.For<IFontFiles>();
        var payload = CatalogFontFixture();
        var destination = Path.Combine("synthetic-fonts", "CodeAssist-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)) + ".ttf");
        var notice = Path.Combine(destination + ".notices", "archive", "LICENSE");
        files.Exists(destination).Returns(preexisting);
        files.Exists(notice).Returns(preexisting);
        files.OpenRead(destination).Returns(_ => new MemoryStream(payload));
        files.OpenRead(notice).Returns(_ => new MemoryStream(new byte[] { 1 }));
        files.RefreshAsync(Arg.Any<FontPlatform>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(new UnauthorizedAccessException()));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => Host(files).RegisterAsync(new FontInstallation("AnnotationM NFM", payload,
            new Dictionary<string, byte[]> { ["archive/LICENSE"] = [1] }), Cancellation));
        if (preexisting)
        {
            files.DidNotReceive().WriteNew(Arg.Any<string>(), Arg.Any<byte[]>());
            files.DidNotReceive().Delete(Arg.Any<string>());
        }
        else
        {
            files.Received(1).Delete(destination);
            files.Received(1).Delete(notice);
        }
    }

    [Fact]
    public async Task Should_FindSelectedActualFamilyLocally_When_InstalledFacesIncludeOtherFamilies()
    {
        var files = Substitute.For<IFontFiles>();
        files.EnumerateFonts("shared-fonts").Returns(["shared-fonts/other.ttf", "shared-fonts/selected.ttf"]);
        files.OpenRead("shared-fonts/other.ttf").Returns(_ => new MemoryStream(CatalogFontFixture("FiraCode Nerd Font Mono")));
        files.OpenRead("shared-fonts/selected.ttf").Returns(_ => new MemoryStream(CatalogFontFixture()));
        (await Host(files).FindInstalledAsync(null, new("AnnotationMono.zip", "AnnotationM", 100, null), Cancellation)).ShouldBe("AnnotationM NFM");
        (await Host(files).FindInstalledAsync("Missing NFM", null, Cancellation)).ShouldBeNull();
        files.DidNotReceive().WriteNew(Arg.Any<string>(), Arg.Any<byte[]>());
    }

    [Fact]
    public void Should_ReloadSelectedFamilyPerTerminal_When_PersistingV2Confirmation()
    {
        var files = Substitute.For<IFontFiles>();
        var preferences = new FontPreferences(files, "synthetic-state", "terminal-one");
        var state = FontPreferences.Confirm("AnnotationM NFM");
        preferences.Write(state);
        files.Received(1).WriteAtomic(preferences.PathName, Arg.Is<byte[]>(bytes => Encoding.ASCII.GetString(bytes) == state));
        files.Exists(preferences.PathName).Returns(true);
        files.OpenRead(preferences.PathName).Returns(_ => new MemoryStream(Encoding.ASCII.GetBytes(state)));
        FontPreferences.ConfirmedFamily(new FontPreferences(files, "synthetic-state", "terminal-one").Read()).ShouldBe("AnnotationM NFM");
        new FontPreferences(files, "synthetic-state", "terminal-two").Read().ShouldBeNull();
    }

    [Theory]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/123/456", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123/456", true)]
    [InlineData("http://release-assets.githubusercontent.com/github-production-release-asset/123/456", false)]
    [InlineData("https://release-assets.githubusercontent.com.evil.invalid/github-production-release-asset/123/456", false)]
    [InlineData("https://release-assets.githubusercontent.com/other", false)]
    [InlineData("https://user@release-assets.githubusercontent.com/github-production-release-asset/123/456", false)]
    [InlineData("https://release-assets.githubusercontent.com:8443/github-production-release-asset/123/456", false)]
    public void Should_AllowOnlyOfficialHttpsAssetRedirects_When_ValidatingAddress(string url, bool expected)
    {
        HttpFontSource.AllowedAddress(new Uri(url), true, true).ShouldBe(expected);
        HttpFontSource.AllowedAddress(new Uri(url), true, false).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_FollowOnlyAllowlistedAssetRedirect_When_OfficialDownloadReturns302(bool unsafeRedirect)
    {
        var requests = new List<Uri>();
        var pack = new FontPack("AnnotationMono.zip", "AnnotationM", 4, "sha256:" + TestPin.Sha256);
        var source = new HttpFontSource(send: (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(requests.Count == 1 ? System.Net.HttpStatusCode.Found : System.Net.HttpStatusCode.OK);
            if (requests.Count == 1) response.Headers.Location = new Uri(unsafeRedirect ? "https://evil.invalid/archive.zip" : "https://release-assets.githubusercontent.com/github-production-release-asset/123/456");
            else response.Content = new ByteArrayContent(Bytes);
            return Task.FromResult(response);
        });
        if (unsafeRedirect)
        {
            await Should.ThrowAsync<InvalidDataException>(() => source.OpenAsync(pack, Cancellation));
            requests.Count.ShouldBe(1);
        }
        else
        {
            await using var input = await source.OpenAsync(pack, Cancellation);
            using var output = new MemoryStream();
            await input.CopyToAsync(output, Cancellation);
            output.ToArray().ShouldBe(Bytes);
            requests.Select(address => address.AbsoluteUri).ShouldBe([pack.Url, "https://release-assets.githubusercontent.com/github-production-release-asset/123/456"]);
        }
    }

    [Fact]
    public void Should_PinEverySupplementalNotice_When_InspectingAttributionProvenance()
    {
        FontNotices.Sources.Count.ShouldBe(23);
        FontNotices.Sources.Select(notice => notice.Name).Distinct().Count().ShouldBe(23);
        foreach (var notice in FontNotices.Sources)
        {
            Convert.FromHexString(notice.Sha256).Length.ShouldBe(32);
            HttpFontSource.AllowedAddress(new Uri(notice.Url), false, false).ShouldBeTrue();
        }
        FontNotices.Sources.ShouldContain(notice => notice.Name == "src/glyphs/codicons/LICENSE.txt" && notice.Sha256 == "d6239afa918961b465b07bf7411cbe34ff6685854f58553db7966f4881a0211f");
    }

    [Fact]
    public async Task Should_BlockChangedSupplementalNotice_When_OfficialDocumentHashFails()
    {
        var requests = new List<string>();
        var source = new HttpFontSource(send: (request, token) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("changed license") });
        });
        await Should.ThrowAsync<InvalidDataException>(() => source.NoticesAsync(Cancellation));
        requests.ShouldBe(["https://raw.githubusercontent.com/ryanoasis/nerd-fonts/v3.5.1/LICENSE"]);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing-location")]
    [InlineData("metadata-size")]
    public async Task Should_StopBoundedNetworkOperation_When_ResponseIsUnusable(string partition)
    {
        var requests = 0;
        var source = new HttpFontSource(send: (request, token) =>
        {
            requests++;
            var response = new HttpResponseMessage(partition == "metadata-size" ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.Found);
            if (partition == "cycle") response.Headers.Location = request.RequestUri;
            if (partition == "metadata-size") response.Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]);
            return Task.FromResult(response);
        });
        if (partition == "metadata-size") await Should.ThrowAsync<InvalidDataException>(() => source.CatalogAsync(Cancellation));
        else await Should.ThrowAsync<InvalidDataException>(() => source.OpenAsync(new("AnnotationMono.zip", "AnnotationM", 4, "sha256:" + TestPin.Sha256), Cancellation));
        requests.ShouldBe(partition == "cycle" ? 5 : 1);
    }

    [Theory]
    [InlineData("font")]
    [InlineData("notice")]
    [InlineData("family")]
    [InlineData("path")]
    public async Task Should_LeaveUnrelatedFontFilesUntouched_When_SelectedRegistrationConflicts(string partition)
    {
        var files = Substitute.For<IFontFiles>();
        var payload = CatalogFontFixture();
        var destination = Path.Combine("synthetic-fonts", "CodeAssist-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)) + ".ttf");
        var notice = Path.Combine(destination + ".notices", "archive", "LICENSE");
        files.Exists(destination).Returns(partition == "font");
        files.Exists(notice).Returns(true);
        files.OpenRead(destination).Returns(_ => new MemoryStream(new byte[] { 9 }));
        files.OpenRead(notice).Returns(_ => new MemoryStream(new byte[] { partition == "notice" ? (byte)9 : (byte)1 }));
        var installation = new FontInstallation(partition == "family" ? "FiraCode Nerd Font Mono" : "AnnotationM NFM", payload,
            new Dictionary<string, byte[]> { [partition == "path" ? "../LICENSE" : "archive/LICENSE"] = [1] });
        await Should.ThrowAsync<InvalidDataException>(() => Host(files).RegisterAsync(installation, Cancellation));
        files.DidNotReceive().Delete(destination);
        files.DidNotReceive().Delete(notice);
        await files.DidNotReceive().RefreshAsync(Arg.Any<FontPlatform>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int Consumed { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            Consumed += count;
            return count;
        }
    }

    private static byte[] FontFixture(int format, string partition = "valid")
    {
        var bytes = new byte[512];
        void Write16(int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), (ushort)value);
        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
        void Table(int index, string tag, uint offset, uint length)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 12 + index * 16);
            Write32(20 + index * 16, offset);
            Write32(24 + index * 16, length);
        }
        Write32(0, 0x00010000);
        Write16(4, 3);
        Table(0, "post", 64, 16);
        Table(1, "name", partition == "bad-table" ? uint.MaxValue : 80, 100);
        Table(2, "cmap", 192, 320);
        Write32(76, partition == "proportional" ? 0u : 1u);
        Write16(82, 1);
        Write16(84, 18);
        Write16(86, 3);
        Write16(88, 1);
        Write16(92, 16);
        var name = Encoding.BigEndianUnicode.GetBytes(partition == "wrong-family" ? "Wrong font family" : "JetBrainsMono Nerd Font Mono");
        Write16(94, name.Length);
        name.CopyTo(bytes, 98);
        Write16(194, 1);
        Write16(196, 3);
        Write16(198, format == 12 ? 10 : 1);
        Write32(200, 12);
        Write16(204, format);
        var glyphs = new[] { 0xea60, 0xea76, 0xeb08, 0xec0f, 0xf044, 0xf07b, 0xf15c, 0xf489 };
        if (format == 12)
        {
            Write32(208, 112);
            Write32(216, 8);
            for (var index = 0; index < glyphs.Length; index++)
            {
                Write32(220 + index * 12, (uint)glyphs[index]);
                Write32(224 + index * 12, (uint)glyphs[index]);
                Write32(228 + index * 12, partition == "missing-glyph" && index == 2 ? 0u : 1u);
            }
        }
        else
        {
            var mapped = partition.StartsWith("mapped", StringComparison.Ordinal);
            Write16(206, mapped ? 96 : 80);
            Write16(210, 16);
            for (var index = 0; index < glyphs.Length; index++)
            {
                Write16(218 + index * 2, glyphs[index]);
                Write16(236 + index * 2, glyphs[index]);
                if (partition == "missing-glyph" && index == 2) Write16(252 + index * 2, -glyphs[index]);
                if (mapped)
                {
                    Write16(252 + index * 2, 1);
                    Write16(268 + index * 2, 16);
                    Write16(284 + index * 2, partition == "mapped-zero" && index == 2 ? 0 : 1);
                }
            }
        }
        return partition == "truncated" ? bytes[..24] : bytes;
    }
}