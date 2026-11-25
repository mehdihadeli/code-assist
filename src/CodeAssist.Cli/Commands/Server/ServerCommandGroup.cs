using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Server;

public sealed class ServerCommandGroup : ICommandGroup
{
    public int Order => 40;

    public void Register(IConfigurator configurator)
    {
        configurator.AddCommand<ServeCommand>("serve")
            .WithDescription("Start the authenticated local HTTP agent server.")
            .WithExample("serve", "--listen", "127.0.0.1:8080");
        configurator.AddCommand<RequestCommand>("request")
            .WithDescription("Send an explicit authenticated API request to the selected local service.");
    }
}