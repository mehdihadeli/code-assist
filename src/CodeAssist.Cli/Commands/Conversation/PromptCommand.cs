using System.ComponentModel;
using System.Text.Json;
using CodeAssist.Cli.Hosting;
using CodeAssist.Tui;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class PromptCommandSettings : ConnectionSettings
{
    [CommandArgument(0, "<ACTION>"), Description("list or expand; neither calls a model.")]
    public string Action { get; init; } = "";
    [CommandArgument(1, "[NAME]"), Description("Qualified prompt/, skill/ or plugin/ identity.")]
    public string? Name { get; init; }
    [CommandOption("--input <TEXT>"), Description("Untrusted task input, never a shell expression.")]
    public string Input { get; init; } = "";
    [CommandOption("--values <JSON>"), Description("JSON object of declared string argument values.")]
    public string? Values { get; init; }
}

public sealed class PromptCommand(CliContext cli) : CliCommand<PromptCommandSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, PromptCommandSettings settings, CancellationToken cancellation)
    {
        if (settings.Action is not ("list" or "expand")) throw new UsageException("Choose commands list or commands expand.");
        if (settings.Action == "list" && (settings.Name is not null || settings.Input.Length != 0 || settings.Values is not null))
            throw new UsageException("Command listing accepts no prompt data.");
        if (settings.Action == "expand" && string.IsNullOrWhiteSpace(settings.Name)) throw new UsageException("Provide a qualified command name.");
        var values = settings.Values is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(settings.Values)
            ?? throw new UsageException("Command values must be a JSON object.");
        await Cli.ConnectAsync(settings.Endpoint, cancellation);
        var json = Cli.WantsJson(settings.Json);
        if (settings.Action == "list") ConversationView.Commands(Cli.Console, await Cli.Api.CommandsAsync(cancellation), json);
        else ConversationView.Data(Cli.Console, "command.expanded", await Cli.Api.ExpandCommandAsync(new(settings.Name!, settings.Input, values), cancellation), json);
        return ExitCodes.Success;
    }
}

public sealed class PromptCommandGroup : ICommandGroup
{
    public int Order => 35;
    public void Register(IConfigurator configurator) => configurator.AddCommand<PromptCommand>("commands")
        .WithDescription("Discover native operations and expand scoped prompt guidance without inference.");
}