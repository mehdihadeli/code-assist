using System.Globalization;
using CodeAssist.Tui.Themes;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CodeAssist.Tui.FullScreen;

public enum WorkspaceMode { Interactive, Plan, Autopilot }

public sealed record WorkspaceTab(string Id, string Title);

public sealed record WorkspaceShellState
{
    public string Version { get; init; } = "";
    public string Directory { get; init; } = "";
    public string? Branch { get; init; }
    public string? Model { get; init; }
    public WorkspaceMode Mode { get; init; }
    public string ApprovalPolicy { get; init; } = "Ask approval";
    public IReadOnlyList<WorkspaceTab> Tabs { get; init; } = [new("new", "New session")];
    public string ActiveTabId { get; init; } = "new";
    public string Draft { get; init; } = "";
    public int? CaretIndex { get; init; }
    public string? ContextUsage { get; init; }
    public string Status { get; init; } = "No model session";
    public bool Unicode { get; init; } = true;
    public bool NerdIcons { get; init; }
    public string? FocusId { get; init; }
    public bool ExplorerOpen { get; init; }
    public bool ChangesOpen { get; init; }
    public bool ContextOpen { get; init; }
    public bool SessionsOpen { get; init; }
    public bool WorkspaceAccepted { get; init; }
    public IReadOnlyList<WorkspaceTab> RecentSessions { get; init; } = [];
    public IReadOnlyList<WorkspaceFileEntry> Files { get; init; } = [];
    public IReadOnlyList<WorkspaceFileEntry> Changes { get; init; } = [];
    public string? ExplorerStatus { get; init; }
    public string? ChangesStatus { get; init; }
    public string? PreviewPath { get; init; }
    public IReadOnlyList<string> PreviewLines { get; init; } = [];
    public int ScrollOffset { get; init; }
    public string? Feedback { get; init; }
    public IReadOnlyList<string> ConversationLines { get; init; } = [];
}

public sealed record WorkspaceFileEntry(string Path, string? Status = null)
{
    public string? Group { get; init; }
}

public static class WorkspaceShell
{
    private static readonly string[] Banner =
    [
        "   ___         _       _          _    _",
        "  / __|___  __| |___  /_\\   _____(_)__| |_",
        " | (__/ _ \\/ _` / -_)/ _ \\ (_-<_-< (_-<  _|",
        "  \\___\\___/\\__,_\\___/_/ \\_\\/__/__/_/__/\\__|"
    ];

    public static Color Accent(WorkspaceMode mode) => mode switch
    {
        WorkspaceMode.Interactive => TerminalTheme.Activity,
        WorkspaceMode.Plan => TerminalTheme.Success,
        WorkspaceMode.Autopilot => TerminalTheme.Selection,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static bool IsSupported(IAnsiConsole console, TextReader input, bool json) =>
        !json && ReferenceEquals(input, Console.In) && console.Profile.Capabilities.Interactive &&
        console.Profile.Capabilities.Ansi && console.Profile.Width >= 20 && console.Profile.Height >= 17;

    public static Task<string?> ReadAsync(IAnsiConsole console, WorkspaceShellState state, CancellationToken cancellation = default)
        => ReadAsync(console, new WorkspaceShellController(state), cancellation);

    public static async Task<string?> ReadAsync(IAnsiConsole console, WorkspaceShellController controller, CancellationToken cancellation = default,
        Func<WorkspaceShellController, CancellationToken, Task>? onAction = null, IWorkspaceInput? input = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var buffer = new System.Text.StringBuilder(controller.Draft);
        var caret = controller.Caret;
        string? submitted = null;
        void Submit()
        {
            submitted = buffer.ToString();
            controller.Draft = "";
            controller.Caret = 0;
        }
        using var terminalInput = input ?? new WorkspaceTerminalInput(console);
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<WorkspaceInputEvent?>? pending = null;
        async Task<WorkspaceInputEvent?> ReadKey() => await terminalInput.ReadAsync(inputCancellation.Token);
        IRenderable Frame()
        {
            var current = controller.State with { Draft = buffer.ToString(), CaretIndex = caret };
            return console.Profile.Width >= 20 && console.Profile.Height >= 17
                ? Render(current, console.Profile.Width, console.Profile.Height - 1)
                : new Text("> " + Fit(current.Draft, Math.Max(0, console.Profile.Width - 2)));
        }

        try
        {
            await console.Live(Frame()).AutoClear(true).StartAsync(async context =>
            {
                context.Refresh();
                pending = ReadKey();
                var size = (console.Profile.Width, console.Profile.Height);
                var refresh = Task.Delay(100, inputCancellation.Token);
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    await Task.WhenAny(pending, refresh);
                    var changed = pending.IsCompleted || size != (console.Profile.Width, console.Profile.Height);
                    if (pending.IsCompleted)
                    {
                        if (await pending is not { } inputEvent) return;
                        controller.Draft = buffer.ToString();
                        controller.Caret = caret;
                        var targets = console.Profile.Width >= 20 && console.Profile.Height >= 17
                            ? HitTargets(controller.State, console.Profile.Width, console.Profile.Height - 1) : [];
                        var navigated = false;
                        if (inputEvent.Key is null)
                        {
                            if (inputEvent.Scroll != 0) controller.Scroll(inputEvent.Scroll * 3);
                            else if (targets.LastOrDefault(target => target.Contains(inputEvent.Column, inputEvent.Row)) is { } target)
                            {
                                if (target.Id == "send") { Submit(); return; }
                                controller.Activate(target.Id, console.Profile.Width);
                            }
                            navigated = true;
                        }
                        else if (controller.FocusId == "send" && inputEvent.Key.Value.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
                        {
                            Submit();
                            return;
                        }
                        else navigated = controller.HandleNavigation(inputEvent.Key.Value, targets, console.Profile.Width);
                        if (navigated)
                        {
                            buffer.Clear().Append(controller.Draft);
                            caret = controller.Caret;
                            context.UpdateTarget(Frame());
                            context.Refresh();
                            if (onAction is not null) await onAction(controller, cancellation);
                            context.UpdateTarget(Frame());
                            context.Refresh();
                            pending = ReadKey();
                            continue;
                        }
                        var key = inputEvent.Key!.Value;
                        var value = buffer.ToString();
                        var boundaries = StringInfo.ParseCombiningCharacters(value).Append(value.Length).Distinct().ToArray();
                        var index = Array.BinarySearch(boundaries, caret);
                        if (index < 0) index = ~index;
                        var previous = boundaries[Math.Max(0, index - 1)];
                        var next = boundaries[Math.Min(boundaries.Length - 1, index + 1)];
                        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            if (key.Key == ConsoleKey.C) throw new OperationCanceledException(cancellation);
                            if (key.Key is ConsoleKey.D or ConsoleKey.Z && buffer.Length == 0) return;
                        }
                        else switch (key.Key)
                        {
                            case ConsoleKey.Enter when !key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                                Submit();
                                return;
                            case ConsoleKey.Enter:
                                buffer.Insert(caret++, '\n');
                                break;
                            case ConsoleKey.LeftArrow: caret = previous; break;
                            case ConsoleKey.RightArrow: caret = next; break;
                            case ConsoleKey.UpArrow: caret = MoveLine(value, caret, -1); break;
                            case ConsoleKey.DownArrow: caret = MoveLine(value, caret, 1); break;
                            case ConsoleKey.Home: caret = value.LastIndexOf('\n', Math.Max(0, caret - 1), caret) + 1; break;
                            case ConsoleKey.End:
                                var end = value.IndexOf('\n', caret);
                                caret = end < 0 ? value.Length : end;
                                break;
                            case ConsoleKey.Backspace when caret > 0:
                                buffer.Remove(previous, caret - previous);
                                caret = previous;
                                break;
                            case ConsoleKey.Delete when caret < buffer.Length:
                                buffer.Remove(caret, next - caret);
                                break;
                            default:
                                if (!char.IsControl(key.KeyChar)) buffer.Insert(caret++, key.KeyChar);
                                break;
                        }
                        if (buffer.Length > 65536) { submitted = buffer.ToString(); return; }
                        controller.Draft = buffer.ToString();
                        controller.Caret = caret;
                        pending = ReadKey();
                    }
                    if (refresh.IsCompleted) refresh = Task.Delay(100, inputCancellation.Token);
                    if (changed)
                    {
                        size = (console.Profile.Width, console.Profile.Height);
                        context.UpdateTarget(Frame());
                        context.Refresh();
                    }
                }
            });
        }
        finally
        {
            await inputCancellation.CancelAsync();
            if (pending is { IsCompleted: false })
            {
                try { await pending; }
                catch (OperationCanceledException) { }
            }
        }
        return submitted;
    }

    public static IRenderable Render(WorkspaceShellState state, int width, int height)
        => BuildFrame(state, width, height).View;

    public static IReadOnlyList<WorkspaceHitTarget> HitTargets(WorkspaceShellState state, int width, int height)
        => BuildFrame(state, width, height).Targets;

    private static (IRenderable View, IReadOnlyList<WorkspaceHitTarget> Targets) BuildFrame(WorkspaceShellState state, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 20);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);
        var accent = Accent(state.Mode);
        var rows = new List<IRenderable>();
        var targets = new List<WorkspaceHitTarget>();
        var active = state.Tabs.FirstOrDefault(tab => tab.Id == state.ActiveTabId);
        var title = active?.Title ?? "New session";
        var workspace = Path.GetFileName(state.Directory.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(workspace)) workspace = state.Directory;
        var sessions = "Sessions [" + (state.Tabs.Count + state.RecentSessions.Count) + "]";
        var header = " " + Icon(state, "\uec20", "CA") + " CodeAssist  " + state.Version + "  " + workspace;
        var branch = state.Branch is null ? "" : " / " + state.Branch;
        var headerRoom = Math.Max(0, width - sessions.GetCellWidth() - 2);
        header = Fit(header, headerRoom);
        branch = Fit(branch, Math.Max(0, headerRoom - header.GetCellWidth()));
        rows.Add(new StyledLine(
            new Segment(header, Style(TerminalTheme.Text)),
            new Segment(branch, Style(TerminalTheme.Activity)),
            new Segment(new string(' ', Math.Max(0, width - header.GetCellWidth() - branch.GetCellWidth() - sessions.GetCellWidth() - 1)), Style(TerminalTheme.Background)),
            new Segment(sessions + " ", ControlStyle(state, "sessions", state.SessionsOpen))));
        targets.Add(new("sessions", width - sessions.GetCellWidth() - 1, 0, sessions.GetCellWidth()));
        rows.Add(Rule(state, width));

        var stacked = width < 72;
        var controls = new[]
        {
            (Id: "explorer", Label: Icon(state, "\ueaf0", "F") + " Explorer", Open: state.ExplorerOpen),
            (Id: "changes", Label: Icon(state, "\uea68", "=") + " Changes" + (state.Changes.Count == 0 ? "" : " " + state.Changes.Select(file => file.Path).Distinct().Count()), Open: state.ChangesOpen),
            (Id: "context", Label: Icon(state, "\uea74", "i") + " Context", Open: state.ContextOpen)
        };
        var toolsWidth = controls.Sum(control => control.Label.GetCellWidth() + 2);
        if (toolsWidth > width) controls = controls.Take(2).ToArray();
        toolsWidth = Math.Min(width, controls.Sum(control => control.Label.GetCellWidth() + 2));
        var tabWidth = stacked ? width : width - toolsWidth;
        var tabSegments = new List<Segment>();
        var tabColumn = 0;
        var tabBudget = Math.Max(0, tabWidth - 4);
        var open = state.Tabs.ToArray();
        var activeIndex = Math.Max(0, Array.FindIndex(open, tab => tab.Id == state.ActiveTabId));
        var startIndex = 0;
        while (startIndex < activeIndex && open.Skip(startIndex).Take(activeIndex - startIndex + 1).Sum(tab => Math.Min(24, tab.Title.GetCellWidth() + 5)) > tabBudget)
            startIndex++;
        foreach (var tab in open.Skip(startIndex))
        {
            var room = Math.Min(24, tabBudget - tabColumn);
            if (room < 6) break;
            var label = " " + Fit(tab.Title, room - 4) + " ";
            var close = Icon(state, "\uea76", "x") + " ";
            var selected = tab.Id == state.ActiveTabId;
            var background = selected ? new Color(33, 27, 44) : new Color(18, 18, 22);
            tabSegments.Add(new(label, new Style(selected ? TerminalTheme.Text : TerminalTheme.Muted, background,
                state.FocusId == "tab:" + tab.Id ? Decoration.Underline : Decoration.None)));
            tabSegments.Add(new(close, new Style(TerminalTheme.Muted, background)));
            targets.Add(new("tab:" + tab.Id, tabColumn, 2, label.GetCellWidth()));
            targets.Add(new("close:" + tab.Id, tabColumn + label.GetCellWidth(), 2, close.GetCellWidth()));
            tabColumn += label.GetCellWidth() + close.GetCellWidth();
        }
        var add = " " + Icon(state, "\uea60", "+") + " ";
        tabSegments.Add(new(add, ControlStyle(state, "new", true)));
        targets.Add(new("new", tabColumn, 2, add.GetCellWidth()));
        tabColumn += add.GetCellWidth();
        tabSegments.Add(new(new string(' ', Math.Max(0, tabWidth - tabColumn)), new Style(TerminalTheme.Muted, new Color(18, 18, 22))));
        var toolSegments = new List<Segment>();
        var toolColumn = width - toolsWidth;
        foreach (var control in controls)
        {
            var label = Fit(" " + control.Label + " ", width - toolColumn);
            toolSegments.Add(new(label, ControlStyle(state, control.Id, control.Open)));
            targets.Add(new(control.Id, toolColumn, stacked ? 3 : 2, label.GetCellWidth()));
            toolColumn += label.GetCellWidth();
        }
        rows.Add(new StyledLine(tabSegments.Concat(stacked ? [] : toolSegments).ToArray()));
        if (stacked) rows.Add(new StyledLine(new[] { new Segment(new string(' ', width - toolsWidth), Style(TerminalTheme.Background)) }.Concat(toolSegments).ToArray()));
        var underline = new List<Segment>();
        for (var cell = 0; cell < width; cell++)
        {
            var selected = targets.Any(target => (target.Id == "tab:" + state.ActiveTabId || target.Id == "close:" + state.ActiveTabId) && target.Contains(cell, 2));
            underline.Add(new(state.Unicode ? "─" : "-", Style(selected ? TerminalTheme.Selection : TerminalTheme.Border)));
        }
        rows.Add(new StyledLine(underline.ToArray()));

        var bodyStart = rows.Count;
        var leftPanel = state.SessionsOpen ? "sessions" : state.ExplorerOpen ? "explorer" : null;
        var rightPanel = state.ChangesOpen ? "changes" : state.ContextOpen ? "context" : null;
        var narrowPanel = width < 100 ? rightPanel ?? leftPanel : null;
        if (narrowPanel is not null)
        {
            rows.AddRange(PanelRows(state, narrowPanel, width, height - bodyStart, 0, bodyStart, targets));
            return (new Rows(rows), targets);
        }
        var leftWidth = leftPanel is null ? 0 : 26;
        var rightWidth = rightPanel is null ? 0 : 28;
        var centerWidth = width - leftWidth - rightWidth;
        var innerWidth = centerWidth - 4;
        var bodyHeight = height - bodyStart - 7;
        var body = new List<IRenderable>();
        var bannerWidth = Banner.Max(line => line.GetCellWidth()) + 1;
        if (state.PreviewPath is not null)
        {
            body.Add(Pair(state.PreviewPath, Icon(state, "\uea76", "x"), innerWidth));
            targets.Add(new("preview.close", leftWidth + centerWidth - 4, bodyStart, 2));
            body.Add(Line("Read-only", innerWidth, TerminalTheme.Muted));
            body.AddRange(state.PreviewLines.Take(bodyHeight - 2).Select(line => Line(line, innerWidth)));
        }
        else if (state.ConversationLines.Count > 0)
        {
            body.AddRange(state.ConversationLines.TakeLast(bodyHeight).Select(line => Line(line, innerWidth)));
        }
        else if (innerWidth >= bannerWidth && bodyHeight >= 10)
        {
            body.Add(Line("", innerWidth));
            foreach (var line in Banner)
            {
                body.Add(new StyledLine(
                    new Segment(line[..20], Style(TerminalTheme.Text)),
                    new Segment((" " + line[20..]).PadRightCells(innerWidth - 20), Style(TerminalTheme.Activity))));
            }
        }
        else body.Add(Line("CodeAssist", innerWidth, TerminalTheme.Activity));
        if (state.PreviewPath is null && state.ConversationLines.Count == 0)
        {
            body.Add(Line("", innerWidth));
            body.Add(Line(state.Directory, innerWidth, TerminalTheme.Muted));
            body.Add(Line("", innerWidth));
            body.Add(Rule(state, innerWidth));
            body.Add(Line("Recent sessions", innerWidth, TerminalTheme.Muted));
            if (state.RecentSessions.Count == 0) body.Add(Line("No sessions in this workspace.", innerWidth, TerminalTheme.Muted));
            else foreach (var recent in state.RecentSessions)
            {
                if (body.Count >= bodyHeight) break;
                targets.Add(new("reopen:" + recent.Id, leftWidth + 2, bodyStart + body.Count, innerWidth));
                body.Add(Line(recent.Title, innerWidth));
            }
        }
        var center = body.Take(bodyHeight).ToList();
        while (center.Count < bodyHeight) center.Add(Line("", innerWidth));
        if (state.Feedback is not null && center.Count > 0) center[^1] = Line(state.Feedback, innerWidth, TerminalTheme.Warning);
        center.Add(Pair(title, "Context / " + (state.ContextUsage ?? "not reported"), innerWidth, TerminalTheme.Muted));
        center.Add(Rule(state, innerWidth));
        var draftLines = state.Draft.Replace("\r", "").Split('\n');
        var caretIndex = Math.Clamp(state.CaretIndex ?? state.Draft.Length, 0, state.Draft.Length);
        var caretLine = state.Draft[..caretIndex].Count(character => character == '\n');
        var firstLine = state.CaretIndex is null ? 0 : Math.Max(0, caretLine - 1);
        var visibleDraft = draftLines.Skip(firstLine).Take(2).ToArray();
        for (var index = 0; index < 2; index++)
        {
            var value = index < visibleDraft.Length ? visibleDraft[index] : "";
            if (index == 0 && state.Draft.Length == 0) value = "What would you like to work on?";
            var stripe = new Segment(state.Unicode ? "\u258e " : "| ", Style(accent, TerminalTheme.Surface));
            if (state.CaretIndex is not null && index + firstLine == caretLine)
            {
                var lineStart = state.Draft.LastIndexOf('\n', Math.Max(0, caretIndex - 1), caretIndex) + 1;
                var column = state.Draft.Length == 0 ? 0 : caretIndex - lineStart;
                var start = 0;
                var character = column < value.Length ? StringInfo.GetNextTextElement(value, column) : " ";
                while (start < column && value[start..column].GetCellWidth() + character.GetCellWidth() > innerWidth - 2)
                    start += StringInfo.GetNextTextElement(value, start).Length;
                var before = value[start..column];
                var remaining = innerWidth - 2 - before.GetCellWidth() - character.GetCellWidth();
                var after = column < value.Length ? value[(column + character.Length)..] : "";
                center.Add(new StyledLine(stripe,
                    new Segment(Clean(before), Style(TerminalTheme.Text, TerminalTheme.Surface)),
                    new Segment(Clean(character), new Style(TerminalTheme.Surface, TerminalTheme.Text)),
                    new Segment(Fit(after, remaining).PadRightCells(remaining), Style(TerminalTheme.Text, TerminalTheme.Surface))));
            }
            else center.Add(new StyledLine(stripe,
                new Segment(Fit(value, innerWidth - 2).PadRightCells(innerWidth - 2), Style(state.Draft.Length == 0 ? TerminalTheme.Muted : TerminalTheme.Text, TerminalTheme.Surface))));
        }
        var send = Icon(state, "\uec0f", ">");
        var model = Fit(state.Model ?? "Choose a model", Math.Max(0, innerWidth - 3 - send.GetCellWidth()));
        var modelSuffix = state.Model is null ? "  Setup required" : "";
        modelSuffix = Fit(modelSuffix, Math.Max(0, innerWidth - 3 - send.GetCellWidth() - model.GetCellWidth()));
        center.Add(new StyledLine(
            new Segment(state.Unicode ? "\u258e " : "| ", Style(accent, TerminalTheme.Surface)),
            new Segment(model, Style(TerminalTheme.Activity, TerminalTheme.Surface)),
            new Segment(modelSuffix.PadRightCells(innerWidth - 3 - send.GetCellWidth() - model.GetCellWidth()), Style(TerminalTheme.Warning, TerminalTheme.Surface)),
            new Segment(" " + send, Style(accent, TerminalTheme.Surface))));
        center.Add(Rule(state, innerWidth));
        var footerLeft = state.Mode + "  " + state.ApprovalPolicy + (state.WorkspaceAccepted ? "  " + Icon(state, "\ueab2", "+") + " Workspace accepted" : "");
        center.Add(Pair(footerLeft, "/ commands  " + state.Status, innerWidth, accent));
        targets.Add(new("composer", leftWidth + 2, height - 5, innerWidth - 2, 3));
        targets.Add(new("send", leftWidth + centerWidth - 4, height - 3, 2));
        var left = leftPanel is null ? [] : PanelRows(state, leftPanel, leftWidth, height - bodyStart, 0, bodyStart, targets).ToArray();
        var right = rightPanel is null ? [] : PanelRows(state, rightPanel, rightWidth, height - bodyStart, width - rightWidth, bodyStart, targets).ToArray();
        for (var row = 0; row < center.Count; row++)
        {
            var parts = new List<(IRenderable View, int Width)>();
            if (leftWidth > 0) parts.Add((left[row], leftWidth));
            parts.Add((Line("", 2), 2));
            parts.Add((center[row], innerWidth));
            parts.Add((Line("", 2), 2));
            if (rightWidth > 0) parts.Add((right[row], rightWidth));
            rows.Add(new JoinedLine(parts));
        }
        return (new Rows(rows), targets);
    }

    private static Style ControlStyle(WorkspaceShellState state, string id, bool selected) => new(
        selected ? TerminalTheme.Selection : TerminalTheme.Muted,
        selected ? new Color(33, 27, 44) : new Color(18, 18, 22),
        state.FocusId == id ? Decoration.Underline : Decoration.None);

    private static IEnumerable<IRenderable> PanelRows(WorkspaceShellState state, string panel, int width, int height, int column, int row, List<WorkspaceHitTarget> targets)
    {
        var lines = new List<IRenderable> { Line("", width) };
        lines.Add(Pair(char.ToUpperInvariant(panel[0]) + panel[1..], Icon(state, "\uea76", "x"), width));
        targets.Add(new(panel, column + width - 3, row + 1, 2));
        lines.Add(Rule(state, width));
        if (panel == "context")
        {
            foreach (var text in new[] { "Workspace", state.Directory, "", "Model", state.Model ?? "Not selected", "", "Context usage", state.ContextUsage ?? "Not reported", "", "Cost", "Not reported", "", "Approval policy", state.ApprovalPolicy })
                lines.Add(Line(text, width, TerminalTheme.Muted));
        }
        else if (panel == "sessions")
        {
            foreach (var tab in state.Tabs.Concat(state.RecentSessions))
            {
                targets.Add(new("reopen:" + tab.Id, column, row + lines.Count, width));
                lines.Add(Line(tab.Title, width, tab.Id == state.ActiveTabId ? TerminalTheme.Selection : TerminalTheme.Text));
            }
        }
        else
        {
            var status = panel == "explorer" ? state.ExplorerStatus : state.ChangesStatus;
            if (status is not null) lines.Add(Line(status, width, TerminalTheme.Muted));
            var entries = panel == "explorer" ? state.Files : state.Changes;
            if (entries.Count == 0) lines.Add(Line(panel == "explorer" ? "No files loaded." : "No changes loaded.", width, TerminalTheme.Muted));
            foreach (var entry in entries.Skip(state.ScrollOffset))
            {
                if (lines.Count >= height) break;
                if (panel == "changes" && entry.Group is not null && (lines.Count == 3 || entries.IndexOfEntry(entry) == state.ScrollOffset || entries[entries.IndexOfEntry(entry) - 1].Group != entry.Group))
                    lines.Add(Line(entry.Group, width, TerminalTheme.Muted));
                if (lines.Count >= height) break;
                var glyph = entry.Path.EndsWith('/') ? "\ueaf7" : entry.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "\ue7b2" : entry.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "\uea7b" : "\uea7b";
                var label = Icon(state, glyph, "F") + " " + entry.Path;
                targets.Add(new("file:" + entry.Path, column, row + lines.Count, width));
                lines.Add(Pair(label, entry.Status ?? "", width));
            }
        }
        while (lines.Count < height) lines.Add(Line("", width));
        return lines.Take(height);
    }

    private static int IndexOfEntry(this IReadOnlyList<WorkspaceFileEntry> entries, WorkspaceFileEntry entry)
    {
        for (var index = 0; index < entries.Count; index++) if (entries[index] == entry) return index;
        return 0;
    }

    private static string Icon(WorkspaceShellState state, string glyph, string fallback) => state.Unicode && state.NerdIcons ? glyph : fallback;

    private static int MoveLine(string value, int caret, int direction)
    {
        var start = value.LastIndexOf('\n', Math.Max(0, caret - 1), caret) + 1;
        var column = value[start..caret].GetCellWidth();
        var target = direction < 0
            ? start == 0 ? -1 : value.LastIndexOf('\n', Math.Max(0, start - 2), start - 1) + 1
            : value.IndexOf('\n', caret) is var end && end >= 0 ? end + 1 : -1;
        if (target < 0) return caret;
        var cells = 0;
        while (target < value.Length && value[target] != '\n')
        {
            var element = StringInfo.GetNextTextElement(value, target);
            if (cells + element.GetCellWidth() > column) break;
            cells += element.GetCellWidth();
            target += element.Length;
        }
        return target;
    }

    private static Style Style(Color foreground, Color? background = null) => new(foreground, background ?? TerminalTheme.Background);

    private static Text Rule(WorkspaceShellState state, int width) => new(new string(state.Unicode ? '\u2500' : '-', width), Style(TerminalTheme.Border));

    private static Text Line(string value, int width, Color? color = null) => new(Fit(value, width).PadRightCells(width), Style(color ?? TerminalTheme.Text));

    private static IRenderable Pair(string left, string right, int width, Color? color = null)
    {
        right = Fit(right, width - 2);
        left = Fit(left, Math.Max(0, width - right.GetCellWidth() - 2));
        return new StyledLine(
            new Segment(left.PadRightCells(width - right.GetCellWidth() - 1), Style(color ?? TerminalTheme.Text)),
            new Segment(right + " ", Style(TerminalTheme.Muted)));
    }

    private static string Clean(string value) => string.Concat(value.Select(character => char.IsControl(character) ? ' ' : character));

    private static string Fit(string value, int cells)
    {
        value = Clean(value);
        if (value.GetCellWidth() <= cells) return value;
        var elements = StringInfo.GetTextElementEnumerator(value);
        var result = new System.Text.StringBuilder();
        var used = 0;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var size = element.GetCellWidth();
            if (used + size > cells) break;
            result.Append(element);
            used += size;
        }
        return result.ToString();
    }

    private static string PadRightCells(this string value, int cells) => value + new string(' ', Math.Max(0, cells - value.GetCellWidth()));

    private sealed class StyledLine(params Segment[] segments) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth)
        {
            var width = segments.Sum(segment => segment.Text.GetCellWidth());
            return new Measurement(width, width);
        }

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => segments;
    }

    private sealed class JoinedLine(IReadOnlyList<(IRenderable View, int Width)> parts) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(parts.Sum(part => part.Width), parts.Sum(part => part.Width));
        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => parts.SelectMany(part => part.View.Render(options, part.Width));
    }
}