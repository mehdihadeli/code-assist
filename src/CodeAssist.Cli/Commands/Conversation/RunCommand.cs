using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class RunSettings : ConnectionSettings
{
    [CommandArgument(0, "[PROMPT]"), Description("Prompt text, quoted as one argument. Mutually exclusive with --stdin.")]
    public string? Prompt { get; init; }

    [CommandOption("--stdin"), Description("Read one bounded prompt from standard input until EOF.")]
    public bool Stdin { get; init; }

    [CommandOption("--session <ID>"), Description("Continue an existing session; requires --endpoint.")]
    public Guid? Session { get; init; }

    [CommandOption("--latest"), Description("Continue the newest session in the explicitly attached service.")]
    public bool Latest { get; init; }

    [CommandOption("--fork"), Description("Fork the selected --session or --latest before sending the prompt.")]
    public bool Fork { get; init; }

    [CommandOption("--file <PATH>"), Description("Attach an explicit UTF-8 text file as untrusted context. Repeatable; total prompt remains bounded.")]
    public string[] Files { get; init; } = [];
}

public sealed class RunCommand(CliContext cli, TextReader input) : CliCommand<RunSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, RunSettings settings, CancellationToken cancellation)
    {
        if (settings.Stdin == (settings.Prompt is not null)) throw new UsageException("Supply a quoted prompt or --stdin, but not both.");
        ConversationOutput.ValidateSession(settings.Session, settings.Endpoint, settings.Model);
        ConversationOutput.ValidateSelection(settings.Session, settings.Latest, settings.Fork, settings.Endpoint, settings.Model);
        var prompt = settings.Stdin ? await ReadPromptAsync(cancellation) : settings.Prompt!;
        if (string.IsNullOrWhiteSpace(prompt)) throw new UsageException("Prompt must not be empty.");
        if (prompt.Length > 65536) throw new UsageException("Prompt exceeds 65536 characters.");
        if (settings.Files.Length > 16) throw new UsageException("At most 16 text attachments are allowed.");
        foreach (var path in settings.Files)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 65536) throw new UsageException("Text attachment exceeds 65536 bytes.");
            var bytes = new byte[65537];
            var count = 0;
            int read;
            while (count < bytes.Length && (read = await file.ReadAsync(bytes.AsMemory(count), cancellation)) != 0) count += read;
            if (count > 65536) throw new UsageException("Text attachment exceeds 65536 bytes.");
            string content;
            try { content = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count); }
            catch (System.Text.DecoderFallbackException) { throw new UsageException("Attachments must contain valid UTF-8 text."); }
            if (content.Contains('\0')) throw new UsageException("Binary attachments are not supported.");
            var header = $"\n\nUntrusted attachment {Path.GetFileName(path)}:\n";
            if ((long)prompt.Length + header.Length + content.Length > 65536) throw new UsageException("Prompt and attachments exceed 65536 characters.");
            prompt += header + content;
        }
        await Cli.ConnectAsync(settings.Endpoint, cancellation);
        var session = await ConversationOutput.SelectAsync(Cli, settings.Session, settings.Latest, settings.Fork, settings.Model, cancellation);
        var json = Cli.WantsJson(settings.Json);
        if (json) ConversationOutput.WriteEvent(Cli, new { kind = "session.selected", session });
        return await ConversationOutput.RenderAsync(Cli, session.Id, prompt, json, cancellation);
    }

    private async Task<string> ReadPromptAsync(CancellationToken cancellation)
    {
        var prompt = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            if (prompt.Length + count > 65536) throw new UsageException("Prompt exceeds 65536 characters.");
            prompt.Append(buffer, 0, count);
        }
        return prompt.ToString();
    }
}