using CodeAssist.Tui.FullScreen;
using CodeAssist.Tui.Themes;
using CodeAssist.Contracts;
using NSubstitute;
using System.Runtime.InteropServices;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;

namespace CodeAssist.Cli.IntegrationTests.Commands;

public sealed class WorkspaceShellTests
{
    [Fact]
    public async Task Should_RenderInitialShell_When_DraftIsSubmittedWithoutEditing()
    {
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);

        (await WorkspaceShell.ReadAsync(console, new WorkspaceShellState(), TestContext.Current.CancellationToken)).ShouldBe("");
        console.Output.ShouldContain("CodeAssist");
        console.Output.ShouldContain("Explorer");
        console.Output.ShouldContain("Ask approval");
    }

    [Theory]
    [InlineData(true, true, true, 80, 24, true, false)]
    [InlineData(false, true, true, 80, 24, true, true)]
    [InlineData(false, false, true, 80, 24, true, false)]
    [InlineData(false, true, false, 80, 24, true, false)]
    [InlineData(false, true, true, 19, 24, true, false)]
    [InlineData(false, true, true, 80, 16, true, false)]
    [InlineData(false, true, true, 80, 24, false, false)]
    public void Should_PreserveFallback_When_TerminalDoesNotSupportShell(bool json, bool interactive, bool ansi, int width, int height, bool standardInput, bool expected)
    {
        var console = InteractiveConsole();
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Profile.Capabilities.Interactive = interactive;
        console.Profile.Capabilities.Ansi = ansi;

        WorkspaceShell.IsSupported(console, standardInput ? Console.In : TextReader.Null, json).ShouldBe(expected);
    }

    [Fact]
    public async Task Should_MoveAcrossDraftLines_When_LiveComposerEditsMultilineInput()
    {
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.UpArrow);
        console.Input.PushKey(ConsoleKey.Home);
        console.Input.PushKey(ConsoleKey.Delete);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.End);
        console.Input.PushText("!");
        console.Input.PushKey(ConsoleKey.Enter);

        (await WorkspaceShell.ReadAsync(console, new WorkspaceShellState { Draft = "first\nsecond" }, TestContext.Current.CancellationToken)).ShouldBe("irst\nsecond!");
    }

    [Fact]
    public async Task Should_KeepEmptyDraftEditable_When_HomeAndEndArePressed()
    {
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Home);
        console.Input.PushKey(ConsoleKey.End);
        console.Input.PushText("ok");
        console.Input.PushKey(ConsoleKey.Enter);

        (await WorkspaceShell.ReadAsync(console, new WorkspaceShellState(), TestContext.Current.CancellationToken)).ShouldBe("ok");
    }

    [Fact]
    public async Task Should_ReturnEditedDraft_When_LiveComposerSubmits()
    {
        var console = InteractiveConsole();
        console.Input.PushText("helo");
        console.Input.PushKey(ConsoleKey.LeftArrow);
        console.Input.PushText("l");
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await WorkspaceShell.ReadAsync(console, new WorkspaceShellState(), TestContext.Current.CancellationToken);

        result.ShouldBe("hello");
        console.Output.ShouldContain("CodeAssist");
        console.Output.ShouldContain("Explorer");
    }

    [Fact]
    public async Task Should_RemoveWholeGrapheme_When_LiveComposerBackspaces()
    {
        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushText("ok");
        console.Input.PushKey(ConsoleKey.Enter);

        (await WorkspaceShell.ReadAsync(console, new WorkspaceShellState { Draft = "e\u0301" }, TestContext.Current.CancellationToken)).ShouldBe("ok");
    }

    [Fact]
    public async Task Should_ObserveCancellationBeforeInput_When_LiveComposerStartsCanceled()
    {
        var console = InteractiveConsole();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => WorkspaceShell.ReadAsync(console, new WorkspaceShellState(), source.Token));
        console.Output.ShouldBeEmpty();
    }

    [Fact]
    public void Should_KeepCaretVisibleWithinBounds_When_DraftScrollsPastViewport()
    {
        var draft = "first\nsecond\nthird " + new string('x', 80) + "end";
        var lines = Render(new WorkspaceShellState { Draft = draft, CaretIndex = draft.Length }, 48, 24);

        lines[^5].ShouldContain("second");
        lines[^4].ShouldContain("end");
        lines.ShouldAllBe(line => line.GetCellWidth() <= 48);
    }

    private static TestConsole InteractiveConsole()
    {
        var console = new TestConsole();
        console.Profile.Width = 80;
        console.Profile.Height = 24;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        return console;
    }

    [Theory]
    [InlineData(20, 16)]
    [InlineData(48, 24)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public void Should_KeepComposerAnchoredWithinCellBounds_When_ViewportChanges(int width, int height)
    {
        var lines = Render(new WorkspaceShellState { Directory = "sample-project" }, width, height);

        lines.Length.ShouldBe(height);
        lines.ShouldAllBe(line => line.GetCellWidth() <= width);
        lines[^3].ShouldContain("Choose a model"[..Math.Min(14, width - 8)]);
        lines[^5].ShouldStartWith("  \u258e ");
        lines[^4].ShouldStartWith("  \u258e ");
        lines[^2].ShouldBe("  " + new string('\u2500', width - 4) + "  ");
        lines[^1].ShouldStartWith("  ");
        if (width >= 48) lines[^1].ShouldContain("Interactive");
    }

    [Fact]
    public void Should_GroupWorkspaceControlsAfterSessionActions_When_RenderingWideShell()
    {
        var state = new WorkspaceShellState { Directory = "sample-project", Branch = "main" };

        var lines = Render(state, 120, 32);

        lines[0].ShouldContain("CA CodeAssist");
        lines[0].ShouldContain("sample-project / main");
        lines[0].TrimEnd().ShouldEndWith("Sessions [1]");
        lines[1].ShouldBe(new string('\u2500', 120));
        lines[2].ShouldContain(" New session x  +");
        lines[2].IndexOf("Explorer", StringComparison.Ordinal).ShouldBeGreaterThan(lines[2].IndexOf('+'));
        lines[2].TrimEnd().ShouldEndWith("Explorer  = Changes  i Context");
    }

    [Theory]
    [InlineData(20, 16)]
    [InlineData(48, 24)]
    [InlineData(80, 32)]
    [InlineData(120, 40)]
    public void Should_FillEveryRow_When_RenderingOverLightTerminalBackground(int width, int height)
    {
        var lines = Render(new WorkspaceShellState { Directory = "sample-project" }, width, height);

        lines.Length.ShouldBe(height);
        lines.ShouldAllBe(line => line.GetCellWidth() == width);
    }

    [Fact]
    public void Should_KeepWorkspaceControlsTogether_When_NarrowShellWrapsStrip()
    {
        var lines = Render(new WorkspaceShellState(), 48, 24);

        lines[2].ShouldContain("New session");
        lines[3].Trim().ShouldBe("F Explorer  = Changes  i Context");
        lines[4].ShouldBe(new string('\u2500', 48));
    }

    [Theory]
    [InlineData(WorkspaceMode.Interactive, 121, 180, 255)]
    [InlineData(WorkspaceMode.Plan, 141, 206, 168)]
    [InlineData(WorkspaceMode.Autopilot, 191, 154, 255)]
    public void Should_UseApprovedModeAccentWithoutChangingPolicy_When_ModeChanges(WorkspaceMode mode, byte red, byte green, byte blue)
    {
        var state = new WorkspaceShellState { Mode = mode, ApprovalPolicy = "Accept edits" };

        var lines = Render(state, 120, 24);

        WorkspaceShell.Accent(mode).ShouldBe(new Color(red, green, blue));
        lines[^1].ShouldStartWith("  " + mode + "  Accept edits");
        state.ApprovalPolicy.ShouldBe("Accept edits");
        TerminalTheme.Border.ShouldBe(new Color(54, 53, 63));
    }

    [Theory]
    [InlineData(WorkspaceMode.Interactive, "121;180;255")]
    [InlineData(WorkspaceMode.Plan, "141;206;168")]
    [InlineData(WorkspaceMode.Autopilot, "191;154;255")]
    public void Should_ColorOnlyModeStripeAndSendWithNeutralBorders_When_RenderingAnsi(WorkspaceMode mode, string rgb)
    {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(output)
        });
        console.Profile.Width = 80;
        console.Profile.Height = 24;

        console.Write(WorkspaceShell.Render(new WorkspaceShellState { Mode = mode, Draft = "literal" }, 80, 24));
        var lines = output.ToString().Replace("\r", "").TrimEnd('\n').Split('\n');

        lines[^5].ShouldContain("38;2;" + rgb);
        lines[^5].ShouldContain("38;2;232;231;237");
        lines[^3].ShouldContain("38;2;" + rgb);
        lines[^1].ShouldContain("38;2;" + rgb);
        lines[^6].ShouldContain("38;2;54;53;63");
        lines[^2].ShouldContain("38;2;54;53;63");
        lines[^6].ShouldNotContain("38;2;" + rgb);
        lines[^2].ShouldNotContain("38;2;" + rgb);
    }

    [Fact]
    public void Should_PreserveMarkupAsLiteralAndBoundUnicodeText_When_DraftContainsUntrustedContent()
    {
        var state = new WorkspaceShellState { Draft = "[bold]literal[/]\n\u754c\u754c e\u0301 " + new string('x', 100), Directory = "project\u001b[31m" };

        var lines = Render(state, 48, 24);

        lines[^5].ShouldContain("[bold]literal[/]");
        lines[^4].ShouldContain("\u754c\u754c e\u0301");
        lines.ShouldAllBe(line => line.GetCellWidth() <= 48);
        string.Join('\n', lines).ShouldNotContain("\u001b");
    }

    [Fact]
    public void Should_UseOnlyAscii_When_UnicodeIsDisabledEvenWithNerdIconsRequested()
    {
        var lines = Render(new WorkspaceShellState { Unicode = false, NerdIcons = true }, 80, 24);

        string.Join('\n', lines).ShouldAllBe(character => character <= 127);
        lines[0].ShouldContain("CA CodeAssist");
        lines[3].ShouldBe(new string('-', 80));
        lines[^5].ShouldStartWith("  | ");
        lines[^3].TrimEnd().ShouldEndWith(">");
    }

    [Fact]
    public void Should_RequireExplicitNerdGlyphOptIn_When_UnicodeIsSupported()
    {
        var fallback = Render(new WorkspaceShellState(), 80, 24);
        var nerd = Render(new WorkspaceShellState { NerdIcons = true }, 80, 24);

        fallback[0].ShouldContain("CA CodeAssist");
        nerd[0].ShouldContain("\uec20 CodeAssist");
        nerd[2].ShouldContain("\uea60");
        nerd[^3].ShouldContain("\uec0f");
    }

    [Fact]
    public void Should_KeepJoinedBannerAndUnknownMetadata_When_HomeHasNoModel()
    {
        var lines = Render(new WorkspaceShellState(), 80, 32);

        lines[5].TrimEnd().ShouldBe("     ___         _        _          _    _");
        lines[^7].ShouldContain("Context / not reported");
        lines[^3].ShouldContain("Setup required");
        lines[^1].ShouldContain("No model session");
        string.Join('\n', lines).ShouldNotContain("Service ready");
    }

    [Theory]
    [InlineData(19, 16)]
    [InlineData(80, 15)]
    public void Should_RejectUnsupportedViewport_When_TooSmallForShell(int width, int height)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => WorkspaceShell.Render(new WorkspaceShellState(), width, height));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(71)]
    [InlineData(72)]
    [InlineData(80)]
    [InlineData(120)]
    public void Should_KeepActiveTabReachableAndRowsExact_When_OpenTabsOverflow(int width)
    {
        var state = new WorkspaceShellState
        {
            Tabs = Enumerable.Range(1, 8).Select(index => new WorkspaceTab("session-" + index, "Conversation " + index)).ToArray(),
            ActiveTabId = "session-8"
        };
        var lines = Render(state, width, 24);
        lines.Length.ShouldBe(24);
        lines.ShouldAllBe(line => line.GetCellWidth() == width);
        var targets = WorkspaceShell.HitTargets(state, width, 24);
        targets.ShouldContain(target => target.Id == "tab:session-8");
        targets.ShouldContain(target => target.Id == "close:session-8");
        targets.ShouldAllBe(target => target.Column >= 0 && target.Column + target.Width <= width && target.Row >= 0 && target.Row + target.Height <= 24);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_SubmitDraftWithoutContamination_When_SendIsActivatedByMouseOrKeyboard(bool keyboard)
    {
        var console = InteractiveConsole();
        var controller = new WorkspaceShellController(new() { Draft = "send [literal]" });
        var targets = WorkspaceShell.HitTargets(controller.State, 80, 23);
        var send = targets.Single(target => target.Id == "send");
        var composer = targets.Single(target => target.Id == "composer");
        composer.Contains(send.Column - 1, send.Row).ShouldBeTrue();
        composer.Contains(send.Column, send.Row).ShouldBeFalse();
        targets.Last(target => target.Contains(send.Column, send.Row)).Id.ShouldBe("send");
        using var input = new QueuedWorkspaceInput(
            keyboard ? new WorkspaceInputEvent(Key(ConsoleKey.F6)) : new(null, send.Column, send.Row),
            keyboard ? new WorkspaceInputEvent(Key(ConsoleKey.Enter)) : null);
        var result = await WorkspaceShell.ReadAsync(console, controller, TestContext.Current.CancellationToken, input: input);
        result.ShouldBe("send [literal]");
        controller.Draft.ShouldBeEmpty();
        controller.Caret.ShouldBe(0);
        input.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_SharePanelActivationWithoutDraftContamination_When_InputNavigatesControls(bool keyboard)
    {
        var console = InteractiveConsole();
        var controller = new WorkspaceShellController(new() { Draft = "retained" });
        var explorer = WorkspaceShell.HitTargets(controller.State, 80, 23).Single(target => target.Id == "explorer");
        var actions = new List<string?>();
        using var input = new QueuedWorkspaceInput(keyboard
            ? [new(Key(ConsoleKey.F6)), new(Key(ConsoleKey.F6)), new(Key(ConsoleKey.F6)), new(Key(ConsoleKey.F6)), new(Key(ConsoleKey.F6)), new(Key(ConsoleKey.Spacebar, ' ')), null]
            : [new(null, explorer.Column, explorer.Row), null]);
        await WorkspaceShell.ReadAsync(console, controller, TestContext.Current.CancellationToken,
            (current, _) => { actions.Add(current.RequestedPanel); current.CompleteAction(); return Task.CompletedTask; }, input);
        controller.ExplorerOpen.ShouldBeTrue();
        actions.Where(action => action is not null).ShouldBe(["explorer"]);
        controller.Draft.ShouldBe("retained");
        input.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Should_RetainSessionDraftAndHistory_When_TabsCloseReopenAndDeduplicate()
    {
        var controller = new WorkspaceShellController(new());
        var first = new AgentSessionDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), "provider/first", DateTimeOffset.UnixEpoch, true, 3);
        controller.BindSession(first);
        controller.Draft = "first draft";
        controller.Caret = 5;
        controller.ConversationLines = ["first answer"];
        controller.NewSession();
        var second = new AgentSessionDto(Guid.Parse("22222222-2222-2222-2222-222222222222"), "provider/second", DateTimeOffset.UnixEpoch, false, 1);
        controller.BindSession(second);
        controller.Draft = "second draft";
        controller.Activate("close:tab-1", 120);
        controller.Session.ShouldBe(second);
        controller.State.RecentSessions.Select(tab => tab.Id).ShouldBe(["tab-1"]);
        controller.Activate("reopen:tab-1", 120);
        controller.Session.ShouldBe(first);
        controller.Session!.Running.ShouldBeTrue();
        controller.State.Model.ShouldBe("provider/first");
        controller.Draft.ShouldBe("first draft");
        controller.Caret.ShouldBe(5);
        controller.ConversationLines.ShouldBe(["first answer"]);
        controller.BindSession(second);
        controller.State.Tabs.Count.ShouldBe(2);
        controller.State.ActiveTabId.ShouldBe("tab-2");
        controller.Draft.ShouldBe("second draft");
        controller.Activate("close:tab-2", 120);
        controller.Activate("close:tab-1", 120);
        controller.State.Tabs.Select(tab => tab.Id).ShouldBe(["tab-3"]);
        controller.Session.ShouldBeNull();
        controller.Draft.ShouldBeEmpty();
        controller.Activate("reopen:tab-2", 120);
        controller.Session.ShouldBe(second);
        controller.Draft.ShouldBe("second draft");
    }

    [Fact]
    public void Should_ActivateTabsAndCycleFocus_When_KeyboardNavigationUsesSharedActions()
    {
        var controller = new WorkspaceShellController(new());
        controller.NewSession();
        controller.NewSession();
        controller.Activate("tab:tab-2", 120);
        foreach (var pair in new[] { (ConsoleKey.Home, "tab-1"), (ConsoleKey.LeftArrow, "tab-3"), (ConsoleKey.RightArrow, "tab-1"), (ConsoleKey.End, "tab-3") })
        {
            controller.HandleNavigation(Key(pair.Item1), WorkspaceShell.HitTargets(controller.State, 120, 24), 120).ShouldBeTrue();
            controller.State.ActiveTabId.ShouldBe(pair.Item2);
        }
        var targets = WorkspaceShell.HitTargets(controller.State, 120, 24);
        controller.HandleNavigation(Key(ConsoleKey.Tab), targets, 120).ShouldBeTrue();
        controller.FocusId.ShouldBe("new");
        controller.HandleNavigation(new ConsoleKeyInfo('\0', ConsoleKey.F6, true, false, false), targets, 120).ShouldBeTrue();
        controller.FocusId.ShouldBe("tab:tab-3");
        controller.HandleNavigation(new ConsoleKeyInfo('\0', ConsoleKey.N, false, true, true), targets, 120).ShouldBeTrue();
        controller.State.ActiveTabId.ShouldBe("tab-4");
        controller.HandleNavigation(new ConsoleKeyInfo('\0', ConsoleKey.W, false, true, true), targets, 120).ShouldBeTrue();
        controller.State.RecentSessions.Select(tab => tab.Id).ShouldBe(["tab-4"]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(71)]
    [InlineData(72)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(120)]
    public void Should_PlacePanelsAndPreserveBounds_When_ViewportCrossesSinglePaneBoundary(int width)
    {
        var controller = new WorkspaceShellController(new() { Directory = "workspace", Files = [new("src/"), new("src/one.cs")], Changes = [new("other.cs", "M") { Group = "Staged" }] });
        controller.Activate("explorer", width);
        controller.Activate("changes", width);
        controller.ExplorerOpen.ShouldBe(width >= 100);
        controller.ChangesOpen.ShouldBeTrue();
        var lines = Render(controller.State, width, 24);
        lines.Length.ShouldBe(24);
        lines.ShouldAllBe(line => line.GetCellWidth() == width);
        var targets = WorkspaceShell.HitTargets(controller.State, width, 24);
        targets.ShouldAllBe(target => target.Column >= 0 && target.Column + target.Width <= width && target.Row + target.Height <= 24);
        var changed = targets.Single(target => target.Id == "file:other.cs");
        changed.Column.ShouldBe(width >= 100 ? width - 28 : 0);
        if (width >= 100) targets.Single(target => target.Id == "file:src/one.cs").Column.ShouldBe(0);
        else targets.ShouldNotContain(target => target.Id == "composer");
        controller.Activate("context", width);
        controller.ChangesOpen.ShouldBeFalse();
        controller.ContextOpen.ShouldBeTrue();
        string.Join('\n', Render(controller.State, width, 24)).ShouldContain("Not reported");
        controller.HandleNavigation(Key(ConsoleKey.Escape), targets, width).ShouldBeTrue();
        controller.ExplorerOpen.ShouldBeFalse();
        controller.ContextOpen.ShouldBeFalse();
    }

    [Fact]
    public void Should_CollapseFoldersAndRequestReadOnlyPreview_When_FileActionsAndPagingOccur()
    {
        var controller = new WorkspaceShellController(new() { Files = Enumerable.Range(0, 30).Select(index => new WorkspaceFileEntry("src/" + index + ".cs")).Prepend(new("src/")).Append(new("README.md")).ToArray() });
        controller.Activate("file:src/", 120);
        controller.State.Files.Select(entry => entry.Path).ShouldBe(["src/", "README.md"]);
        controller.Activate("file:src/", 120);
        controller.State.Files.Count.ShouldBe(32);
        controller.Activate("explorer", 80);
        controller.HandleNavigation(Key(ConsoleKey.PageDown), [], 80).ShouldBeTrue();
        controller.ScrollOffset.ShouldBe(10);
        controller.HandleNavigation(Key(ConsoleKey.PageUp), [], 80).ShouldBeTrue();
        controller.ScrollOffset.ShouldBe(0);
        controller.Scroll(100);
        controller.ScrollOffset.ShouldBe(32);
        controller.Scroll(-100);
        controller.ScrollOffset.ShouldBe(0);
        controller.Activate("file:README.md", 80);
        controller.RequestedPath.ShouldBe("README.md");
        controller.ExplorerOpen.ShouldBeFalse();
        controller.PreviewPath = "README.md";
        controller.PreviewLines = ["[bold]literal[/]\u001b"];
        var lines = Render(controller.State, 80, 24);
        string.Join('\n', lines).ShouldContain("Read-only");
        string.Join('\n', lines).ShouldContain("[bold]literal[/]");
        string.Join('\n', lines).ShouldNotContain("\u001b");
        controller.Activate("preview.close", 80);
        controller.PreviewPath.ShouldBeNull();
        controller.CompleteAction();
        controller.RequestedPath.ShouldBeNull();
    }

    [Fact]
    public async Task Should_DisposeInputAndRetainDraft_When_PendingReadIsCanceled()
    {
        var console = InteractiveConsole();
        var controller = new WorkspaceShellController(new() { Draft = "retained" });
        using var source = new CancellationTokenSource();
        using var input = new QueuedWorkspaceInput();
        input.OnRead = token => { source.Cancel(); return new ValueTask<WorkspaceInputEvent?>(Task.FromCanceled<WorkspaceInputEvent?>(token)); };
        await Should.ThrowAsync<OperationCanceledException>(() => WorkspaceShell.ReadAsync(console, controller, source.Token, input: input));
        input.Disposed.ShouldBeTrue();
        controller.Draft.ShouldBe("retained");
    }

    [Fact]
    public async Task Should_ReadFallbackKeysAndRejectDisposedInput_When_TestConsoleIsInjected()
    {
        var console = InteractiveConsole();
        console.Input.PushText("z");
        var input = new WorkspaceTerminalInput(console);
        var result = await input.ReadAsync(TestContext.Current.CancellationToken);
        result!.Value.Key!.Value.KeyChar.ShouldBe('z');
        result.Value.Column.ShouldBe(-1);
        input.Dispose();
        input.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await input.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Should_FillSelectedTabAndUnderlinePurple_When_MultipleTabsRenderAnsi()
    {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor, Out = new AnsiConsoleOutput(output) });
        console.Profile.Width = 120;
        var state = new WorkspaceShellState { Tabs = [new("first", "First"), new("second", "Second")], ActiveTabId = "second" };
        console.Write(WorkspaceShell.Render(state, 120, 24));
        var lines = output.ToString().Replace("\r", "").TrimEnd('\n').Split('\n');
        lines[2].ShouldContain("48;2;33;27;44");
        lines[2].ShouldContain("Second");
        lines[3].ShouldContain("38;2;191;154;255");
        lines[3].ShouldContain("38;2;54;53;63");
        var plain = Render(state, 120, 24);
        plain[0].ShouldStartWith(" CA CodeAssist");
        plain[0].ShouldNotContain("\u25cf");
        plain[2].ShouldContain(" First x  Second x ");
    }

    [Fact]
    public async Task Should_RestoreEachTabDraft_When_QueuedMouseSwitchesTabsAndComposer()
    {
        var controller = new WorkspaceShellController(new() { Draft = "first" });
        controller.NewSession();
        controller.Draft = "second";
        controller.Caret = 6;
        var targets = WorkspaceShell.HitTargets(controller.State, 80, 23);
        var first = targets.Single(target => target.Id == "tab:tab-1");
        var composer = targets.Single(target => target.Id == "composer");
        using var input = new QueuedWorkspaceInput(new(null, first.Column, first.Row), new(null, composer.Column, composer.Row), new(Key(ConsoleKey.End)), new(Key(ConsoleKey.X, 'x')), new(Key(ConsoleKey.Enter)));
        (await WorkspaceShell.ReadAsync(InteractiveConsole(), controller, TestContext.Current.CancellationToken, input: input)).ShouldBe("firstx");
        controller.Activate("tab:tab-2", 80);
        controller.Draft.ShouldBe("second");
        controller.Caret.ShouldBe(6);
    }

    [Fact]
    public async Task Should_ConsumeMouseWheelWithoutTyping_When_QueuedInputScrollsPanel()
    {
        var controller = new WorkspaceShellController(new() { Draft = "safe", Files = Enumerable.Range(0, 20).Select(index => new WorkspaceFileEntry(index + ".cs")).ToArray() });
        controller.Activate("explorer", 80);
        using var input = new QueuedWorkspaceInput(new(null, 2, 10, 2), null);
        await WorkspaceShell.ReadAsync(InteractiveConsole(), controller, TestContext.Current.CancellationToken, input: input);
        controller.ScrollOffset.ShouldBe(6);
        controller.Draft.ShouldBe("safe");
    }

    [Fact]
    public async Task Should_DecodeNativeRecordsAndRestoreMode_When_InjectedApiReadsKeysMouseAndWheel()
    {
        const uint original = 0x0277;
        var api = Substitute.For<WorkspaceTerminalInput.INativeApi>();
        api.TryGetMode(out Arg.Any<uint>()).Returns(call => { call[0] = original; return true; });
        api.SetMode(Arg.Any<uint>()).Returns(true);
        api.PendingCount().Returns(1u);
        api.WindowTop().Returns(5);
        var records = new Queue<WorkspaceTerminalInput.InputRecord>([
            new() { Type = 1, KeyDown = 0 },
            new() { Type = 1, KeyDown = 1, VirtualKey = (ushort)ConsoleKey.N, Character = 'n', KeyModifiers = 0x1f },
            new() { Type = 2, MouseX = 12, MouseY = 9, Buttons = 1 },
            new() { Type = 2, MouseX = 12, MouseY = 9, Buttons = 1 },
            new() { Type = 2, MouseX = 12, MouseY = 9, Buttons = 0 },
            new() { Type = 2, MouseX = 13, MouseY = 10, Buttons = 1, MouseFlags = 1 },
            new() { Type = 2, MouseX = 13, MouseY = 10, Buttons = 0 },
            new() { Type = 2, MouseX = 14, MouseY = 11, Buttons = 1 },
            new() { Type = 2, MouseX = 14, MouseY = 11, Buttons = 0xff880000, MouseFlags = 4 },
            new() { Type = 2, MouseX = 14, MouseY = 11, Buttons = 0x00780000, MouseFlags = 4 }
        ]);
        api.Read().Returns(_ => records.Dequeue());
        var input = new WorkspaceTerminalInput(InteractiveConsole(), api);
        var key = (await input.ReadAsync(TestContext.Current.CancellationToken))!.Value.Key!.Value;
        key.Key.ShouldBe(ConsoleKey.N);
        key.KeyChar.ShouldBe('n');
        key.Modifiers.ShouldBe(ConsoleModifiers.Shift | ConsoleModifiers.Alt | ConsoleModifiers.Control);
        (await input.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(new WorkspaceInputEvent(null, 12, 4));
        (await input.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(new WorkspaceInputEvent(null, 14, 6));
        (await input.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(new WorkspaceInputEvent(null, 14, 6, 1));
        (await input.ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(new WorkspaceInputEvent(null, 14, 6, -1));
        records.ShouldBeEmpty();
        input.Dispose();
        input.Dispose();
        api.Received(1).SetMode(0x00b8);
        api.Received(1).SetMode(original);
        Marshal.SizeOf<WorkspaceTerminalInput.InputRecord>().ShouldBe(20);
        Marshal.OffsetOf<WorkspaceTerminalInput.InputRecord>(nameof(WorkspaceTerminalInput.InputRecord.Character)).ToInt32().ShouldBe(14);
        Marshal.OffsetOf<WorkspaceTerminalInput.InputRecord>(nameof(WorkspaceTerminalInput.InputRecord.KeyModifiers)).ToInt32().ShouldBe(16);
        Marshal.OffsetOf<WorkspaceTerminalInput.InputRecord>(nameof(WorkspaceTerminalInput.InputRecord.MouseFlags)).ToInt32().ShouldBe(16);
    }

    [Fact]
    public async Task Should_RestoreNativeMode_When_ReadIsCanceledBeforePolling()
    {
        var api = Substitute.For<WorkspaceTerminalInput.INativeApi>();
        api.TryGetMode(out Arg.Any<uint>()).Returns(call => { call[0] = 0x0040u; return true; });
        api.SetMode(Arg.Any<uint>()).Returns(true);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var input = new WorkspaceTerminalInput(InteractiveConsole(), api);
        await Should.ThrowAsync<OperationCanceledException>(async () => await input.ReadAsync(source.Token));
        input.Dispose();
        api.DidNotReceive().PendingCount();
        api.Received(1).SetMode(0x0040);
    }

    [Fact]
    public async Task Should_CancelAndDisposeWithoutLosingDraft_When_ControlCIsPressedWithPanelFocus()
    {
        var controller = new WorkspaceShellController(new() { Draft = "retained" });
        controller.Activate("explorer", 120);
        using var input = new QueuedWorkspaceInput(new(new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true)), null);
        await Should.ThrowAsync<OperationCanceledException>(() => WorkspaceShell.ReadAsync(InteractiveConsole(), controller, TestContext.Current.CancellationToken, input: input));
        controller.Draft.ShouldBe("retained");
        input.Disposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(20, 16)]
    [InlineData(80, 24)]
    [InlineData(120, 24)]
    public void Should_KeepSessionTargetsInsideViewport_When_SessionsPanelOverflows(int width, int height)
    {
        var controller = new WorkspaceShellController(new());
        for (var index = 0; index < 40; index++) controller.NewSession();
        controller.Activate("sessions", width);
        var targets = WorkspaceShell.HitTargets(controller.State, width, height);
        targets.ShouldAllBe(target => target.Column >= 0 && target.Column + target.Width <= width && target.Row >= 0 && target.Row + target.Height <= height);
        Render(controller.State, width, height).ShouldAllBe(line => line.GetCellWidth() == width);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(48)]
    [InlineData(120)]
    public void Should_RenderHostileMetadataLiterallyWithinExactBounds_When_CaretIsVisible(int width)
    {
        const string hostile = "[bold]\u001b[31m\t\b\u754c e\u0301[/]";
        var state = new WorkspaceShellState
        {
            Version = hostile, Directory = hostile, Branch = hostile, Model = hostile,
            Tabs = [new("first", hostile), new("second", "second")], ActiveTabId = "first",
            Draft = hostile, CaretIndex = hostile.Length, ApprovalPolicy = hostile, Status = hostile, Feedback = hostile
        };
        var lines = Render(state, width, 24);
        lines.Length.ShouldBe(24);
        lines.ShouldAllBe(line => line.GetCellWidth() == width);
        string.Join("", lines).ShouldNotContain("\u001b");
        string.Join("", lines).ShouldNotContain("\t");
        string.Join("", lines).ShouldNotContain("\b");
        lines[^5].ShouldContain("[/]");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_FallbackOrFailWithoutRestoringUnchangedMode_When_NativeSetupIsUnavailable(bool modeAvailable)
    {
        var api = Substitute.For<WorkspaceTerminalInput.INativeApi>();
        api.TryGetMode(out Arg.Any<uint>()).Returns(modeAvailable);
        var console = InteractiveConsole();
        console.Input.PushText("f");
        if (modeAvailable)
        {
            Should.Throw<System.ComponentModel.Win32Exception>(() => new WorkspaceTerminalInput(console, api));
            api.Received(1).SetMode(0x0098);
        }
        else
        {
            using var input = new WorkspaceTerminalInput(console, api);
            (await input.ReadAsync(TestContext.Current.CancellationToken))!.Value.Key!.Value.KeyChar.ShouldBe('f');
            input.Dispose();
            api.DidNotReceive().SetMode(Arg.Any<uint>());
        }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0') => new(character, key, false, false, false);

    private sealed class QueuedWorkspaceInput(params WorkspaceInputEvent?[] events) : IWorkspaceInput
    {
        private readonly Queue<WorkspaceInputEvent?> pending = new(events);
        public bool Disposed { get; private set; }
        public Func<CancellationToken, ValueTask<WorkspaceInputEvent?>>? OnRead { get; set; }
        public ValueTask<WorkspaceInputEvent?> ReadAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return OnRead is not null ? OnRead(cancellation) : ValueTask.FromResult(pending.TryDequeue(out var next) ? next : null);
        }
        public void Dispose() => Disposed = true;
    }

    private static string[] Render(WorkspaceShellState state, int width, int height)
    {
        var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Profile.Capabilities.Unicode = state.Unicode;
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        console.Write(WorkspaceShell.Render(state, width, height));
        return console.Output.Replace("\r", "").TrimEnd('\n').Split('\n');
    }
}