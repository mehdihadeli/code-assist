using System.ComponentModel;
using System.Reflection;
using CodeAssist.Cli.Fonts;
using CodeAssist.Cli.Hosting;
using CodeAssist.Configuration;
using CodeAssist.Contracts;
using CodeAssist.Tui;
using CodeAssist.Tui.FullScreen;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class ChatSettings : ConnectionSettings
{
    [CommandOption("--icons <MODE>"), Description("Terminal icons: auto or nerd. A compatible Nerd Font is required for interactive chat.")]
    public string Icons { get; init; } = "auto";

    [CommandOption("--trust-workspace"), Description("Accept workspace access for this chat launch without an interactive prompt.")]
    public bool TrustWorkspace { get; init; }

    [CommandOption("--session <ID>"), Description("Continue an existing session; requires --endpoint.")]
    public Guid? Session { get; init; }

    [CommandOption("--latest"), Description("Continue the newest session in the explicitly attached service.")]
    public bool Latest { get; init; }

    [CommandOption("--fork"), Description("Fork the selected --session or --latest before opening chat.")]
    public bool Fork { get; init; }
}

public sealed class ChatCommand(CliContext cli, TextReader input, ITerminalFontSetup fonts) : CliCommand<ChatSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, ChatSettings settings, CancellationToken cancellation)
    {
        ConversationOutput.ValidateSession(settings.Session, settings.Endpoint, settings.Model);
        ConversationOutput.ValidateSelection(settings.Session, settings.Latest, settings.Fork, settings.Endpoint, settings.Model);
        var icons = settings.Icons.ToLowerInvariant();
        if (icons is not ("auto" or "nerd")) throw new UsageException("Nerd Font is required. Use --icons auto or nerd.");
        var json = Cli.WantsJson(settings.Json);
        if (settings.Endpoint is null && !settings.TrustWorkspace)
        {
            if (json || !Cli.Console.Profile.Capabilities.Interactive)
                throw new UsageException("Local chat requires workspace acceptance. Run interactively or supply --trust-workspace after reviewing the folder.");

            Cli.Console.Write(new Rule("[yellow]Workspace access[/]").LeftJustified());
            Cli.Console.WriteLine();
            Cli.Console.WriteLine(Cli.Settings.Locations.WorkingDirectory);
            Cli.Console.WriteLine();
            Cli.Console.WriteLine("Continue only if you trust this folder and its contents.");
            Cli.Console.WriteLine("CodeAssist may read workspace files and use enabled tools with their configured permissions.");
            Cli.Console.WriteLine("Acceptance applies to this launch only and does not change tool permissions.");
            Cli.Console.WriteLine();
            var choice = await new SelectionPrompt<string>()
                .Title("Allow access to this workspace?")
                .AddChoices("Leave workspace", "Allow for this launch")
                .ShowAsync(Cli.Console, cancellation);
            if (choice != "Allow for this launch") return ExitCodes.Success;
            if (Cli.Console.Profile.Capabilities.Ansi) Cli.Console.Clear();
        }
        var useShell = WorkspaceShell.IsSupported(Cli.Console, input, json);
        var interactive = !json && Cli.Console.Profile.Capabilities.Interactive;
        if (interactive && (!Cli.Console.Profile.Capabilities.Unicode || Cli.Snapshot.Settings.Cli.Unicode == UnicodeSetting.Ascii))
            throw new UsageException("Interactive chat requires Unicode and a Nerd Font. Remove the cli.unicode=ascii override.");
        var nerdIcons = interactive && await fonts.ConfigureAsync(icons, cancellation);
        if (interactive && !nerdIcons) throw new UsageException("Nerd Font setup is required before interactive chat can start.");
        await Cli.ConnectAsync(settings.Endpoint, cancellation);
        var deferred = settings.Endpoint is null && !json;
        var permissions = settings.Endpoint is not null ? "permissions: service" : Cli.Snapshot.Settings.Agent.PermissionPreset switch
        {
            PermissionPreset.ReadOnly => "permissions: readOnly",
            PermissionPreset.AcceptEdits => "permissions: acceptEdits",
            _ => "permissions: ask",
        };
        AgentSessionDto? session = null;
        var version = typeof(ChatCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        if (deferred)
        {
            if (!useShell) ConversationView.Welcome(Cli.Console, version, settings.Model ?? Cli.Snapshot.Settings.Agent.Model,
                Cli.Settings.Locations.WorkingDirectory);
        }
        else
        {
            session = await ConversationOutput.SelectAsync(Cli, settings.Session, settings.Latest, settings.Fork, settings.Model, cancellation);
            ConversationView.Selected(Cli.Console, session, json);
        }
        var shellState = new WorkspaceShellState
        {
            Version = version,
            Directory = Cli.Settings.Locations.WorkingDirectory,
            Model = settings.Model ?? Cli.Snapshot.Settings.Agent.Model,
            ApprovalPolicy = permissions switch
            {
                "permissions: ask" => "Ask approval",
                "permissions: acceptEdits" => "Accept edits",
                "permissions: readOnly" => "Read only",
                _ => "Permissions: service"
            },
            Unicode = Cli.Console.Profile.Capabilities.Unicode,
            NerdIcons = nerdIcons,
            WorkspaceAccepted = settings.Endpoint is null || settings.TrustWorkspace
        };
        var shell = useShell ? new WorkspaceShellController(shellState) : null;
        var workspaceReader = new WorkspacePanelReader(shellState.Directory,
            Cli.Snapshot.Settings.Workspace.Ignore.Concat(Cli.Snapshot.Settings.Workspace.Protected), shellState.WorkspaceAccepted);
        if (shell is not null)
        {
            shell.BindSession(session);
            try { shellState = shellState with { Branch = await workspaceReader.ReadBranchAsync(cancellation) }; }
            catch (Exception exception) when (!cancellation.IsCancellationRequested && exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            shell.UpdateWorkspace(shellState);
        }
        async Task LoadWorkspaceAsync(WorkspaceShellController controller, CancellationToken token)
        {
            try
            {
                if (controller.RequestedPanel == "explorer")
                    shellState = shellState with { Files = await Task.Run(() => workspaceReader.ReadFiles(token), token), ExplorerStatus = "Read-only / up to 256 entries" };
                if (controller.RequestedPanel == "changes")
                    shellState = shellState with { Changes = await workspaceReader.ReadChangesAsync(token), ChangesStatus = "Read-only workspace snapshot" };
                if (controller.RequestedPath is { } path)
                {
                    controller.PreviewLines = await workspaceReader.ReadPreviewAsync(path, token);
                    controller.PreviewPath = path;
                }
                controller.Feedback = null;
            }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                var reason = exception is UnauthorizedAccessException ? "Workspace access required or path protected." : "Read failed / reopen to retry.";
                if (controller.RequestedPanel == "explorer") shellState = shellState with { Files = [], ExplorerStatus = reason };
                else if (controller.RequestedPanel == "changes") shellState = shellState with { Changes = [], ChangesStatus = reason };
                else controller.Feedback = reason;
            }
            finally
            {
                controller.UpdateWorkspace(shellState);
                controller.CompleteAction();
            }
        }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var prompt = useShell
                ? await WorkspaceShell.ReadAsync(Cli.Console, shell!, cancellation, LoadWorkspaceAsync)
                : await ConversationView.ReadAsync(Cli.Console, input, json, permissions, cancellation);
            if (shell is not null) session = shell.Session;
            if (prompt is null) return ExitCodes.Success;
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            if (prompt.Length > 65536) throw new UsageException("Prompt exceeds 65536 characters.");
            try
            {
                if (prompt.TrimStart().StartsWith("//", StringComparison.Ordinal)) prompt = prompt.TrimStart()[1..];
                else if (prompt.TrimStart().StartsWith('/'))
                {
                    var result = await ConversationCommandRouter.ExecuteAsync(Cli, session, prompt.Trim(), json, cancellation, settings.Model);
                    session = result.Session;
                    shell?.BindSession(session);
                    if (result.Exit) return ExitCodes.Success;
                    if (result.Prompt is null) continue;
                    prompt = result.Prompt;
                }
                if (session is null)
                {
                    session = await Cli.Api.CreateAsync(settings.Model, cancellation);
                    shell?.BindSession(session);
                    ConversationView.Selected(Cli.Console, session, json);
                }
            }
            catch (HttpRequestException exception) when (deferred && session is null && exception.StatusCode is
                System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.BadGateway)
            {
                if (shell is not null) { shell.Draft = prompt; shell.Caret = prompt.Length; shell.Feedback = "Model unavailable / draft retained."; }
                Cli.Console.WriteLine("Could not open a model session. Check provider/model settings and credentials, or select /session.new <provider/model>. The service is still running.");
                continue;
            }
            var exitCode = await ConversationOutput.RenderAsync(Cli, session.Id, prompt, json, cancellation);
            if (exitCode != ExitCodes.Success) return exitCode;
            if (shell is not null)
                shell.ConversationLines = (await Cli.Api.MessagesAsync(session.Id, cancellation))
                    .SelectMany(message => new[] { message.Role, message.Text, "" }).SelectMany(line => line.Replace("\r", "").Split('\n')).TakeLast(256).ToArray();
        }
    }
}