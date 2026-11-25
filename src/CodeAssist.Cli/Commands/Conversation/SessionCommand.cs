using System.ComponentModel;
using System.Text.Json;
using CodeAssist.Contracts;
using CodeAssist.Cli.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class SessionSettings : ConnectionSettings
{
    [CommandArgument(0, "<ACTION>"), Description("list, show, cancel, delete, fork, export or import.")]
    public string Action { get; init; } = "";

    [CommandArgument(1, "[ID]"), Description("Session identifier; required except for list.")]
    public Guid? Id { get; init; }

    [CommandOption("--yes"), Description("Confirm deletion of an idle session.")]
    public bool Yes { get; init; }

    [CommandOption("--file <PATH>"), Description("Archive file for export/import; export never overwrites an existing file.")]
    public string? File { get; init; }

    [CommandOption("--limit <COUNT>"), Description("Maximum newest sessions to list.")]
    public int? Limit { get; init; }
}

public sealed class SessionCommand(CliContext cli) : CliCommand<SessionSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, SessionSettings settings, CancellationToken cancellation)
    {
        if (settings.Action is not ("list" or "show" or "cancel" or "delete" or "fork" or "export" or "import")) throw new UsageException("Unknown session action.");
        if (settings.Endpoint is null) throw new UsageException("Session commands require --endpoint to select the service that owns the sessions.");
        if (settings.Action is "list" or "import" && settings.Id is not null) throw new UsageException($"Session {settings.Action} does not accept an ID.");
        if (settings.Action is not ("list" or "import") && settings.Id is null) throw new UsageException("Supply a session ID.");
        if (settings.Action == "import" && settings.File is null) throw new UsageException("Session import requires --file.");
        if (settings.File is not null && settings.Action is not ("import" or "export")) throw new UsageException("--file applies only to import/export.");
        if (settings.Limit is not null && (settings.Action != "list" || settings.Limit is < 1 or > 10000)) throw new UsageException("--limit applies to list and must be between 1 and 10000.");
        if (settings.Action == "delete" && !settings.Yes) throw new UsageException("Deleting session history requires --yes.");
        await Cli.ConnectAsync(settings.Endpoint, cancellation);
        var json = Cli.WantsJson(settings.Json);
        switch (settings.Action)
        {
            case "list":
                var sessions = (await Cli.Api.SessionsAsync(cancellation)).OrderByDescending(session => session.CreatedAt).ThenBy(session => session.Id)
                    .Take(settings.Limit ?? 10000).ToArray();
                if (json) Cli.WriteJson(sessions);
                else
                {
                    var table = new Table().AddColumn("Session").AddColumn("Model").AddColumn("Messages").AddColumn("State");
                    foreach (var session in sessions)
                        table.AddRow(session.Id.ToString(), Markup.Escape(session.Model), session.MessageCount.ToString(), session.Running ? "running" : "idle");
                    Cli.Console.Write(table);
                }
                break;
            case "show":
                var selected = await Cli.Api.SessionAsync(settings.Id!.Value, cancellation);
                var messages = await Cli.Api.MessagesAsync(selected.Id, cancellation);
                if (json) Cli.WriteJson(new { session = selected, messages });
                else
                {
                    Cli.Console.WriteLine($"Session {selected.Id} | {selected.Model}");
                    foreach (var message in messages) Cli.Console.WriteLine($"{message.Role}: {message.Text}");
                }
                break;
            case "cancel":
                var result = await Cli.Api.CancelAsync(settings.Id!.Value, cancellation);
                if (json) Cli.WriteJson(result);
                else Cli.Console.WriteLine(result.Canceled ? "Cancellation requested." : "No active turn.");
                break;
            case "delete":
                await Cli.Api.DeleteSessionAsync(settings.Id!.Value, cancellation);
                if (json) Cli.WriteJson(new { deleted = settings.Id.Value });
                else Cli.Console.WriteLine("Session deleted.");
                break;
            case "fork":
                var forked = await Cli.Api.ForkSessionAsync(settings.Id!.Value, cancellation);
                if (json) Cli.WriteJson(forked);
                else Cli.Console.WriteLine($"Session {forked.Id} forked.");
                break;
            case "export":
                var archive = await Cli.Api.ExportSessionAsync(settings.Id!.Value, cancellation);
                if (settings.File is null) Cli.WriteJson(archive);
                else
                {
                    await using var target = new FileStream(settings.File, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await JsonSerializer.SerializeAsync(target, archive, JsonSerializerOptions.Web, cancellation);
                    if (json) Cli.WriteJson(new { exported = settings.Id.Value });
                    else Cli.Console.WriteLine("Session archive exported. Treat it as sensitive conversation data.");
                }
                break;
            case "import":
                await using (var source = new FileStream(settings.File!, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (source.Length > 393216) throw new UsageException("Session archive exceeds 384 KiB.");
                    var importedArchive = await JsonSerializer.DeserializeAsync<AgentSessionArchive>(source, JsonSerializerOptions.Web, cancellation)
                        ?? throw new UsageException("Invalid session archive.");
                    var imported = await Cli.Api.ImportSessionAsync(importedArchive, cancellation);
                    if (json) Cli.WriteJson(imported);
                    else Cli.Console.WriteLine($"Session {imported.Id} imported.");
                }
                break;
        }
        return ExitCodes.Success;
    }
}