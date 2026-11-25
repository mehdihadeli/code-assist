using CodeAssist.Cli.Hosting;
using CodeAssist.Contracts;
using CodeAssist.Tui;

namespace CodeAssist.Cli.Commands.Conversation;

internal static class ConversationOutput
{
    public static void ValidateSession(Guid? session, Uri? endpoint, string? model)
    {
        if (session is not null && endpoint is null) throw new UsageException("Reusing a session requires --endpoint; managed services are invocation-local.");
        if (session is not null && model is not null) throw new UsageException("An existing session keeps its model. Omit --model or create a new session.");
    }

    public static void ValidateSelection(Guid? session, bool latest, bool fork, Uri? endpoint, string? model)
    {
        if (latest && session is not null) throw new UsageException("Choose --latest or --session, not both.");
        if (latest && endpoint is null) throw new UsageException("--latest requires --endpoint.");
        if (latest && model is not null) throw new UsageException("An existing session keeps its model. Omit --model or create a new session.");
        if (fork && session is null && !latest) throw new UsageException("--fork requires --session or --latest.");
    }

    public static async Task<AgentSessionDto> SelectAsync(CliContext cli, Guid? session, bool latest, bool fork, string? model, CancellationToken cancellation)
    {
        if (latest)
            session = (await cli.Api.SessionsAsync(cancellation)).OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id)
                .FirstOrDefault()?.Id ?? throw new UsageException("No sessions are available in the attached service.");
        if (session is null) return await cli.Api.CreateAsync(model, cancellation);
        return fork ? await cli.Api.ForkSessionAsync(session.Value, cancellation) : await cli.Api.SessionAsync(session.Value, cancellation);
    }

    public static void WriteEvent<T>(CliContext cli, T value) =>
        TurnTranscript.WriteEvent(cli.Console, value);

    public static async Task<int> RenderAsync(CliContext cli, Guid session, string prompt, bool json, CancellationToken cancellation)
    {
        return await TurnTranscript.RenderAsync(cli.Console, cli.Api.StreamAsync(session, prompt, cancellation), json, cancellation) switch
        {
            TurnOutcome.Completed => ExitCodes.Success,
            TurnOutcome.Canceled => ExitCodes.Interrupted,
            _ => ExitCodes.Failure,
        };
    }
}