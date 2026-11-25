using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Conversation;

public sealed class ConversationCommandGroup : ICommandGroup
{
    public int Order => 0;

    public void Register(IConfigurator configurator)
    {
        configurator.AddCommand<AcpCommand>("acp")
            .WithDescription("Bridge Agent Client Protocol text sessions over standard input/output.");
        configurator.AddCommand<RunCommand>("run")
            .WithDescription("Stream one conversation turn, using a prompt or standard input.")
            .WithExample("run", "Summarize this project");
        configurator.AddCommand<ChatCommand>("chat")
            .WithDescription("Open a line-oriented conversation with the shared agent service.");
        configurator.AddCommand<SessionCommand>("sessions")
            .WithDescription("Inspect, cancel, delete, fork and transfer sessions in an explicitly attached service.")
            .WithExample("sessions", "list", "--endpoint", "http://127.0.0.1:8080");
    }
}