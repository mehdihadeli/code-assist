using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Tools;

public sealed class ToolsCommandGroup : ICommandGroup
{
    public int Order => 25;

    public void Register(IConfigurator configurator) =>
        configurator.AddBranch("tools", tools =>
        {
            tools.SetDescription("Inspect the tools the agent can use.");

            tools.AddCommand<ToolsListCommand>("list")
                .WithAlias("ls")
                .WithDescription("List tools with their risk and capability tags.")
                .WithExample("tools", "list", "--role", "planner");
            tools.AddCommand<ToolsShowCommand>("show")
                .WithDescription("Show one tool's description and input schema.");
        });
}
