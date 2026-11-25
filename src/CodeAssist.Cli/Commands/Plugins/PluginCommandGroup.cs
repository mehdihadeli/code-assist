using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Plugins;

public sealed class PluginCommandGroup : ICommandGroup
{
    public int Order => 35;

    public void Register(IConfigurator configurator) => configurator.AddBranch("plugins", branch =>
    {
        branch.SetDescription("Create, inspect and manage digest-approved runtime plugins.");
        branch.AddCommand<PluginCommand>("list").WithDescription("List installed plugin releases, digests and activation state.");
        branch.AddCommand<PluginCommand>("init").WithDescription("Create an original plugin in a new directory; requires --id.");
        branch.AddCommand<PluginCommand>("validate").WithDescription("Validate a plugin directory or ZIP without executing it.");
        branch.AddCommand<PluginCommand>("pack").WithDescription("Package only declared files; requires --out.");
        branch.AddCommand<PluginCommand>("install").WithDescription("Install a directory/ZIP, or a registry with --id; initially disabled.");
        branch.AddCommand<PluginCommand>("enable").WithDescription("Approve and enable an installed plugin; requires --approve SHA256.");
        branch.AddCommand<PluginCommand>("disable").WithDescription("Disable an installed plugin, preserving its files.");
        branch.AddCommand<PluginCommand>("remove").WithDescription("Remove an installation record; dependants must be removed first.");
        branch.AddCommand<PluginCommand>("show").WithDescription("Inspect an installed id or a directory/ZIP, including its context footprint.");
        branch.AddCommand<PluginCommand>("expand").WithDescription("Print enabled text guidance without calling a model.");
        branch.AddCommand<PluginCommand>("publish").WithDescription("Publish an immutable ZIP into a local registry; requires --registry.");
        branch.AddCommand<PluginCommand>("registry").WithDescription("Read an explicit local/HTTPS registry; no automatic registration or installation.");
        branch.AddCommand<PluginCommand>("stats").WithDescription("Show local invocation counts and timestamps, never prompts or replies.");
        branch.AddCommand<PluginCommand>("libraries").WithDescription("List configured registry aliases without accessing their sources.");
        branch.AddCommand<PluginCommand>("search").WithDescription("Search a registry's release lane without downloading packages.");
        branch.AddCommand<PluginCommand>("suggest").WithDescription("Rank uninstalled registry plugins from explicit local activity facts.");
        branch.AddCommand<PluginCommand>("diagnose").WithDescription("Explain activation, policy, content and dependency failures without changing state.");
    });
}