using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Connectors;

public sealed class ConnectorsCommandGroup : ICommandGroup
{
    public int Order => 30;

    public void Register(IConfigurator configurator) =>
        configurator.AddBranch("connectors", connectors =>
        {
            connectors.SetDescription("Manage external tool servers (Model Context Protocol and similar).");

            connectors.AddCommand<ConnectorsListCommand>("list").WithAlias("ls")
                .WithDescription("List configured connectors.");
            connectors.AddCommand<ConnectorsRuntimeCommand>("probe")
                .WithDescription("Explicitly connect, inspect admitted tools, then close the connection.");
            connectors.AddCommand<ConnectorsRuntimeCommand>("authorize")
                .WithDescription("Explicitly connect with trusted browser authorization when required.");
            connectors.AddCommand<ConnectorsRuntimeCommand>("logout")
                .WithDescription("Remove locally cached connector credentials; does not revoke tokens at the authorization server.");
            connectors.AddCommand<ConnectorsRuntimeCommand>("prompts")
                .WithDescription("Connect and list or retrieve untrusted prompt messages; never run a model.");
            connectors.AddCommand<ConnectorsShowCommand>("show")
                .WithDescription("Show one connector.");
            connectors.AddCommand<ConnectorsAddCommand>("add")
                .WithDescription("Add a process or HTTP connector to the user settings.")
                .WithExample("connectors", "add", "files", "--", "npx", "-y", "some-mcp-server")
                .WithExample("connectors", "add", "docs", "--url", "https://mcp.example.com/");
            connectors.AddCommand<ConnectorsRemoveCommand>("remove").WithAlias("rm")
                .WithDescription("Remove a connector from the user settings.");
            connectors.AddCommand<ConnectorsEnableCommand>("enable")
                .WithDescription("Enable a connector.");
            connectors.AddCommand<ConnectorsDisableCommand>("disable")
                .WithDescription("Disable a connector without removing it.");
        });
}
