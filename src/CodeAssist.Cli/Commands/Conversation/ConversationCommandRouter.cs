using System.Text.Json;
using CodeAssist.Cli.Hosting;
using CodeAssist.Contracts;
using CodeAssist.Tui;

namespace CodeAssist.Cli.Commands.Conversation;

internal sealed record ConversationCommandResult(AgentSessionDto? Session, string? Prompt = null, bool Exit = false);

internal static class ConversationCommandRouter
{
    public static async Task<ConversationCommandResult> ExecuteAsync(CliContext cli, AgentSessionDto? session, string line, bool json, CancellationToken cancellation, string? initialModel = null)
    {
        var separator = line.IndexOfAny([' ', '\t']);
        var name = separator < 0 ? line[1..] : line[1..separator];
        var input = separator < 0 ? "" : line[(separator + 1)..].Trim();
        var native = NativeConversationCommands.Items.FirstOrDefault(item => item.Name == name);
        if (native is null)
        {
            if (!name.StartsWith("prompt/", StringComparison.Ordinal) && !name.StartsWith("skill/", StringComparison.Ordinal) && !name.StartsWith("plugin/", StringComparison.Ordinal))
                throw new UsageException("Unknown command. Use /help; use // to send a literal leading slash.");
            if (session is null)
            {
                session = await cli.Api.CreateAsync(initialModel, cancellation);
                ConversationView.Selected(cli.Console, session, json);
            }
            var request = new ExpandConversationCommandRequest(name, input);
            if (input.StartsWith('{'))
            {
                var values = JsonSerializer.Deserialize<PromptValues>(input, new JsonSerializerOptions(JsonSerializerOptions.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                    ?? throw new UsageException("Command values must be a JSON object.");
                request = new(name, values.Input ?? "", values.Values);
            }
            var expansion = await cli.Api.ExpandCommandAsync(request, cancellation);
            if (expansion.Model is not null && expansion.Model != session.Model)
                throw new UsageException("Command requires a different model. Select it explicitly with /session.new before retrying.");
            return new(session, expansion.Text);
        }
        if (native.Hint is null && input.Length != 0) throw new UsageException("This command accepts no arguments.");
        if (session is null && name is not ("exit" or "help" or "session.new" or "session.use" or "session.list" or "models" or "skills" or "plugins" or "status"))
        {
            session = await cli.Api.CreateAsync(initialModel, cancellation);
            ConversationView.Selected(cli.Console, session, json);
        }
        switch (name)
        {
            case "exit": return new(session, Exit: true);
            case "help": ConversationView.Commands(cli.Console, await cli.Api.CommandsAsync(cancellation), json); break;
            case "history": ConversationView.History(cli.Console, await cli.Api.MessagesAsync(session!.Id, cancellation), json); break;
            case "session.new": session = await cli.Api.CreateAsync(input.Length == 0 ? null : input, cancellation); ConversationView.Selected(cli.Console, session, json); break;
            case "session.use":
                if (!Guid.TryParse(input, out var selected)) throw new UsageException("Provide a conversation id.");
                session = await cli.Api.SessionAsync(selected, cancellation); ConversationView.Selected(cli.Console, session, json); break;
            case "session.fork": session = await cli.Api.ForkSessionAsync(session!.Id, cancellation); ConversationView.Selected(cli.Console, session, json); break;
            case "session.list": ConversationView.Data(cli.Console, "sessions.list", await cli.Api.SessionsAsync(cancellation), json); break;
            case "session.rename":
            case "session.archive":
            case "session.restore":
                var current = await cli.Api.SessionDetailsAsync(session!.Id, cancellation);
                if (name == "session.rename" && string.IsNullOrWhiteSpace(input)) throw new UsageException("Provide a conversation title.");
                var updated = await cli.Api.UpdateSessionAsync(session.Id, new(name == "session.rename" ? input : current.Title,
                    name == "session.archive" || name != "session.restore" && current.Archived), cancellation);
                ConversationView.Data(cli.Console, "session.details", updated, json); break;
            case "session.info":
            case "usage": ConversationView.Data(cli.Console, name == "usage" ? "session.usage" : "session.details", await cli.Api.SessionDetailsAsync(session!.Id, cancellation), json); break;
            case "session.checkpoints": ConversationView.Data(cli.Console, "session.checkpoints", await cli.Api.SessionCheckpointsAsync(session!.Id, cancellation), json); break;
            case "session.rewind":
                if (!long.TryParse(input, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) || revision < 1)
                    throw new UsageException("Provide a positive retained conversation revision.");
                var details = await cli.Api.SessionDetailsAsync(session!.Id, cancellation);
                session = await cli.Api.RewindSessionAsync(session.Id, new(revision, details.Revision), cancellation);
                ConversationView.Selected(cli.Console, session, json); break;
            case "context":
                var messages = await cli.Api.MessagesAsync(session!.Id, cancellation);
                ConversationView.Data(cli.Console, "session.context", new { Messages = messages.Length, Characters = messages.Sum(message => (long)message.Text.Length), IncludesHiddenContext = false }, json); break;
            case "models": ConversationView.Data(cli.Console, "models.list", await cli.Api.GetAsync<JsonElement>("/v1/catalog/models", cancellation), json); break;
            case "skills": ConversationView.Data(cli.Console, "skills.list", await cli.Api.GetAsync<JsonElement>("/v1/skills/commands", cancellation), json); break;
            case "plugins": ConversationView.Data(cli.Console, "plugins.list", await cli.Api.GetAsync<JsonElement>("/v1/plugins/commands", cancellation), json); break;
            case "connectors": ConversationView.Data(cli.Console, "connectors.list", await cli.Api.GetAsync<JsonElement>($"/v1/sessions/{session!.Id}/connectors", cancellation), json); break;
            case "status": ConversationView.Data(cli.Console, "service.status", await cli.Api.GetAsync<ServerHealthDto>("/v1/health", cancellation), json); break;
        }
        return new(session);
    }

    private sealed record PromptValues(string? Input, IReadOnlyDictionary<string, string>? Values);
}