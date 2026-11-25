using CodeAssist.Contracts;

namespace CodeAssist.Tui.FullScreen;

public sealed class WorkspaceShellController
{
    private readonly List<TabState> tabs = [];
    private int sequence;
    private string activeId = "";
    private WorkspaceShellState metadata;
    private readonly HashSet<string> collapsedFolders = new(StringComparer.Ordinal);

    public WorkspaceShellController(WorkspaceShellState state)
    {
        metadata = state;
        NewSession();
        Draft = state.Draft;
        Caret = state.CaretIndex ?? state.Draft.Length;
    }

    public AgentSessionDto? Session => Active.Session;
    public string Draft { get => Active.Draft; set => Active.Draft = value; }
    public int Caret { get => Active.Caret; set => Active.Caret = value; }
    public string? FocusId { get; private set; }
    public bool ExplorerOpen { get; private set; }
    public bool ChangesOpen { get; private set; }
    public bool ContextOpen { get; private set; }
    public bool SessionsOpen { get; private set; }
    public string? PreviewPath { get; set; }
    public IReadOnlyList<string> PreviewLines { get; set; } = [];
    public string? RequestedPath { get; private set; }
    public string? RequestedPanel { get; private set; }
    public int ScrollOffset { get; private set; }
    public string? Feedback { get; set; }
    public IReadOnlyList<string> ConversationLines { get => Active.ConversationLines; set => Active.ConversationLines = value; }

    private TabState Active => tabs.First(tab => tab.Tab.Id == activeId);

    public WorkspaceShellState State => metadata with
    {
        Tabs = tabs.Where(tab => !tab.Closed).Select(tab => tab.Tab).ToArray(),
        ActiveTabId = activeId,
        Draft = Draft,
        CaretIndex = Caret,
        Model = Session?.Model ?? metadata.Model,
        Status = Session is null ? "No model session" : "Session selected",
        FocusId = FocusId,
        ExplorerOpen = ExplorerOpen,
        ChangesOpen = ChangesOpen,
        ContextOpen = ContextOpen,
        SessionsOpen = SessionsOpen,
        RecentSessions = tabs.Where(tab => tab.Closed).Select(tab => tab.Tab).ToArray(),
        PreviewPath = PreviewPath,
        PreviewLines = PreviewLines,
        ScrollOffset = ScrollOffset,
        Feedback = Feedback,
        ConversationLines = ConversationLines,
        Files = metadata.Files.Where(entry => !collapsedFolders.Any(folder => entry.Path != folder && entry.Path.StartsWith(folder, StringComparison.Ordinal))).ToArray()
    };

    public void UpdateWorkspace(WorkspaceShellState state) => metadata = state;

    public void BindSession(AgentSessionDto? session)
    {
        if (session is null) return;
        var existing = tabs.FirstOrDefault(tab => tab.Session?.Id == session.Id);
        if (existing is not null && existing != Active)
        {
            existing.Closed = false;
            activeId = existing.Tab.Id;
        }
        Active.Session = session;
        if (Active.Tab.Title.StartsWith("New session", StringComparison.Ordinal))
            Active.Tab = Active.Tab with { Title = "Conversation" };
    }

    public void NewSession()
    {
        sequence++;
        var tab = new TabState(new("tab-" + sequence, sequence == 1 ? "New session" : "New session " + sequence));
        tabs.Add(tab);
        activeId = tab.Tab.Id;
        FocusId = null;
        PreviewPath = null;
    }

    public void Activate(string action, int width)
    {
        RequestedPanel = null;
        RequestedPath = null;
        if (action == "new") NewSession();
        else if (action.StartsWith("tab:", StringComparison.Ordinal) || action.StartsWith("reopen:", StringComparison.Ordinal))
        {
            var id = action[(action.IndexOf(':') + 1)..];
            var selected = tabs.FirstOrDefault(tab => tab.Tab.Id == id);
            if (selected is null) return;
            selected.Closed = false;
            activeId = id;
            PreviewPath = null;
            FocusId = "tab:" + id;
        }
        else if (action.StartsWith("close:", StringComparison.Ordinal))
        {
            var closed = tabs.FirstOrDefault(tab => tab.Tab.Id == action[6..] && !tab.Closed);
            if (closed is null) return;
            closed.Closed = true;
            if (closed.Tab.Id == activeId)
            {
                var remaining = tabs.FirstOrDefault(tab => !tab.Closed);
                if (remaining is null) NewSession();
                else activeId = remaining.Tab.Id;
            }
            FocusId = null;
        }
        else if (action is "explorer" or "changes" or "context" or "sessions")
        {
            var opening = action switch
            {
                "explorer" => !ExplorerOpen,
                "changes" => !ChangesOpen,
                "context" => !ContextOpen,
                _ => !SessionsOpen
            };
            if (opening)
            {
                if (width < 100 || action == "sessions") ExplorerOpen = ChangesOpen = ContextOpen = SessionsOpen = false;
                if (action == "explorer") SessionsOpen = false;
                if (action is "changes" or "context") ChangesOpen = ContextOpen = false;
            }
            switch (action)
            {
                case "explorer": ExplorerOpen = opening; break;
                case "changes": ChangesOpen = opening; break;
                case "context": ContextOpen = opening; break;
                case "sessions": SessionsOpen = opening; break;
            }
            FocusId = action;
            ScrollOffset = 0;
            if (opening) RequestedPanel = action;
        }
        else if (action.StartsWith("file:", StringComparison.Ordinal))
        {
            if (action.EndsWith('/'))
            {
                var folder = action[5..];
                if (!collapsedFolders.Add(folder)) collapsedFolders.Remove(folder);
                return;
            }
            RequestedPath = action[5..];
            if (width < 100) ExplorerOpen = ChangesOpen = false;
            FocusId = null;
        }
        else if (action == "preview.close") PreviewPath = null;
        else if (action == "composer") FocusId = null;
    }

    public void Scroll(int delta) => ScrollOffset = Math.Clamp(ScrollOffset + delta, 0, Math.Max(metadata.Files.Count, metadata.Changes.Count));

    public void CompleteAction() => RequestedPanel = RequestedPath = null;

    public bool HandleNavigation(ConsoleKeyInfo key, IReadOnlyList<WorkspaceHitTarget> targets, int width)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Modifiers.HasFlag(ConsoleModifiers.Alt))
        {
            if (key.Key == ConsoleKey.N) { Activate("new", width); return true; }
            if (key.Key == ConsoleKey.W) { Activate("close:" + activeId, width); return true; }
        }
        if (key.Key is ConsoleKey.F6 or ConsoleKey.Tab)
        {
            var controls = targets.Where(target => !target.Id.StartsWith("close:", StringComparison.Ordinal)).Select(target => target.Id).Distinct().ToArray();
            if (controls.Length == 0) return true;
            var index = Array.IndexOf(controls, FocusId ?? "composer");
            var direction = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1;
            var selected = controls[(index + direction + controls.Length) % controls.Length];
            FocusId = selected == "composer" ? null : selected;
            return true;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            ExplorerOpen = ChangesOpen = ContextOpen = SessionsOpen = false;
            FocusId = null;
            return true;
        }
        if (FocusId is null) return false;
        if (key.Key is ConsoleKey.PageDown or ConsoleKey.PageUp)
        {
            Scroll(key.Key == ConsoleKey.PageDown ? 10 : -10);
            return true;
        }
        if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
        {
            Activate(FocusId, width);
            return true;
        }
        if (FocusId.StartsWith("tab:", StringComparison.Ordinal) && key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.Home or ConsoleKey.End)
        {
            var open = tabs.Where(tab => !tab.Closed).ToArray();
            var index = Array.FindIndex(open, tab => tab.Tab.Id == activeId);
            var selected = key.Key switch
            {
                ConsoleKey.Home => 0,
                ConsoleKey.End => open.Length - 1,
                ConsoleKey.LeftArrow => (index + open.Length - 1) % open.Length,
                _ => (index + 1) % open.Length
            };
            Activate("tab:" + open[selected].Tab.Id, width);
        }
        return true;
    }

    private sealed class TabState(WorkspaceTab tab)
    {
        public WorkspaceTab Tab { get; set; } = tab;
        public AgentSessionDto? Session { get; set; }
        public string Draft { get; set; } = "";
        public int Caret { get; set; }
        public bool Closed { get; set; }
        public IReadOnlyList<string> ConversationLines { get; set; } = [];
    }
}

public sealed record WorkspaceHitTarget(string Id, int Column, int Row, int Width, int Height = 1)
{
    public bool Contains(int column, int row) => column >= Column && column < Column + Width && row >= Row && row < Row + Height;
}