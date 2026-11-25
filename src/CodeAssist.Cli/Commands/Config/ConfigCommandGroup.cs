using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Config;

public sealed class ConfigCommandGroup : ICommandGroup
{
    public int Order => 10;

    public void Register(IConfigurator configurator) =>
        configurator.AddBranch("config", config =>
        {
            config.SetDescription("Inspect and change settings.");

            config.AddCommand<ConfigShowCommand>("show")
                .WithDescription("Show effective settings.")
                .WithExample("config", "show", "--section", "agent", "--sources");
            config.AddCommand<ConfigGetCommand>("get")
                .WithDescription("Print one setting.")
                .WithExample("config", "get", "agent.maxTurns", "--origin");
            config.AddCommand<ConfigSetCommand>("set")
                .WithDescription("Change a setting in the user or project file.")
                .WithExample("config", "set", "agent.maxTurns", "12")
                .WithExample("config", "set", "workspace.ignore", "dist,coverage", "--scope", "project");
            config.AddCommand<ConfigUnsetCommand>("unset")
                .WithDescription("Remove a setting from the user or project file.");
            config.AddCommand<ConfigExplainCommand>("explain")
                .WithDescription("Describe a setting and where its value comes from.")
                .WithExample("config", "explain", "agent.permissionPreset");
            config.AddCommand<ConfigPathCommand>("path")
                .WithDescription("Show where settings files live.");
            config.AddCommand<ConfigValidateCommand>("validate")
                .WithDescription("Check all settings files and report problems.");
            config.AddCommand<ConfigSchemaCommand>("schema")
                .WithDescription("Print the JSON Schema for settings.json.");
            config.AddCommand<ConfigEditCommand>("edit")
                .WithDescription("Open the settings file in $VISUAL or $EDITOR, then validate it.");
        });
}
