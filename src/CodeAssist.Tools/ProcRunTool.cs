using CodeAssist.Tools.Text;
using CodeAssist.Tools;
using CodeAssist.Workspaces.Platform;
using CodeAssist.Contracts;

namespace CodeAssist.Tools;

public sealed class ProcRunTool(IProcessRunner runner, IToolJobs? jobs = null) : ITool
{
    private const string Schema = """
        {"type":"object","properties":{
          "command":{"type":"string","description":"Command line for the host shell."},
          "cwd":{"type":"string","description":"Working directory instead of 'cd' in the command. Defaults to the workspace root."},
          "background":{"type":"boolean","description":"Return a job id instead of waiting; requires a host job service."},
          "shell":{"enum":["default","bash","powershell"],"description":"Named host execution profile, never an arbitrary executable path."},
          "timeoutSeconds":{"type":"integer","minimum":1,"description":"Stop the process tree after this many seconds."}},
         "required":["command"],"additionalProperties":false}
        """;

    public ToolSpec Spec { get; } = new(
        "proc.run",
        "Run a command in the host shell. Wait by default; background=true returns a job id when a host job service is available. Both modes keep timeouts and bounded output. Use cwd instead of cd. Commands run with the user's authority, so prefer narrow, reversible commands.",
        Schema, ToolRisk.Execute, ["exec"]);

    public ValueTask<ToolPlan> PlanAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments, context);
        var split = CommandSplitter.Split(request.Command);
        var effects = split.Segments.Select(segment => new ToolEffect(ToolEffectKind.Exec, segment, request.Cwd.Outside)).ToList();
        if (request.Shell != "default") effects.Add(new(ToolEffectKind.Exec, $"shell:{request.Shell}"));
        if (request.Cwd.Outside) effects.Add(ToolSupport.Effect(ToolEffectKind.Read, request.Cwd));
        var preview = split.Complex
            ? $"{request.Command}\n(contains command or process substitution; judged as one command)"
            : request.Command;
        return new(new ToolPlan(effects, preview));
    }

    public async Task<ToolResult> ExecuteAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Parse(arguments, context);
        if (!Directory.Exists(request.Cwd.Full)) return ToolResult.Error($"Working directory not found: {ToolSupport.Describe(request.Cwd)}");
        if (request.Background)
        {
            if (jobs is null) return ToolResult.Error("Background execution is unavailable: the host has no job service.");
            return await ToolSupport.FromPortAsync(() =>
            {
                var job = jobs.Start(request.Command, token => RunAsync(request, context, token), context, cancellationToken);
                return Task.FromResult(ToolResult.Ok($"Background job: {job.Id}\nCompletion is reported by the host; use job.read for its final result or job.stop to cancel."));
            }, cancellationToken);
        }
        return await ToolSupport.FromPortAsync(() => RunAsync(request, context, cancellationToken), cancellationToken);
    }

    private async Task<ToolResult> RunAsync(ProcessToolRequest request, ToolContext context, CancellationToken cancellationToken)
    {
        var shell = request.Shell == "default" ? ShellLocator.Locate(context.Limits.Shell) :
            ShellLocator.LocateProfile(request.Shell, context.Limits.Shells.GetValueOrDefault(request.Shell));
        var outcome = await runner.RunAsync(new ProcessRequest(
            shell.FilePath, [.. shell.Arguments, request.Command], request.Cwd.Full, request.Timeout,
            context.Outputs is null ? context.Limits.MaxProcessOutputChars : Math.Max(context.Limits.MaxProcessOutputChars, 8 * 1024 * 1024), context.Limits.AllowedEnvironment), cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var budget = OutputBudget.Tail(outcome.Output, context.Limits.MaxProcessOutputChars);
        var omitted = outcome.OmittedChars + budget.OmittedChars;
        var body = omitted > 0 ? $"[{omitted:N0} earlier characters omitted]\n{budget.Text}" : budget.Text;
        Guid? outputId = null;
        if (context.Outputs is not null && omitted > 0)
        {
            var id = await context.Outputs.SaveAsync(context.SessionId, (outcome.OmittedChars > 0 ? "[capture ceiling reached; retained tail follows]\n" : "") + outcome.Output, cancellationToken);
            outputId = id;
            body = $"[Retained output: {id}; use output.read]\n" + body;
        }

        if (outcome.TimedOut)
            return ToolResult.Error($"Timed out after {request.Timeout.TotalSeconds:0} s; the process tree was stopped.\n{body}".TrimEnd()) with { OutputId = outputId };
        if (outcome.ExitCode is null)
            return ToolResult.Error(body.Length == 0 ? "The command could not be run." : body) with { OutputId = outputId };
        var header = outcome.ExitCode == 0 ? "Exit code 0" : $"Exit code {outcome.ExitCode}";
        var text = body.Length == 0 ? $"{header} (no output)" : $"{header}\n{body.TrimEnd()}";
        return (outcome.ExitCode == 0 ? ToolResult.Ok(text) : ToolResult.Error(text)) with { OutputId = outputId };
    }

    private static ProcessToolRequest Parse(ToolArguments arguments, ToolContext context)
    {
        var command = arguments.RequireString("command");
        var cwd = arguments.String("cwd") ?? ".";
        var seconds = arguments.Int("timeoutSeconds", 1, 86_400);
        var background = arguments.Bool("background");
        var shell = arguments.String("shell") ?? "default";
        if (shell is not ("default" or "bash" or "powershell")) arguments.Issue("Unknown shell profile.");
        if (command.Length > 65536 || command.Contains('\0')) arguments.Issue("Command exceeds its size limit or contains NUL.");
        if (seconds > context.Limits.ProcessTimeout.TotalSeconds) arguments.Issue("Requested timeout exceeds the configured execution ceiling.");
        if (command.Length > 0 && string.IsNullOrWhiteSpace(command)) arguments.Issue("'command' must not be blank.");
        arguments.Validate();
        var timeout = seconds is { } s ? TimeSpan.FromSeconds(s) : context.Limits.ProcessTimeout;
        return new ProcessToolRequest(command, ToolSupport.Resolve(cwd, context), timeout, background, shell);
    }
}
