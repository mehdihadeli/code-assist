using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Providers;

public sealed class ProvidersCommandGroup : ICommandGroup
{
    public int Order => 20;

    public void Register(IConfigurator configurator) =>
        configurator.AddBranch("providers", providers =>
        {
            providers.SetDescription("Manage model providers.");

            providers.AddCommand<ProvidersListCommand>("list")
                .WithAlias("ls")
                .WithDescription("List providers, their credentials and readiness.");
            providers.AddCommand<ProvidersShowCommand>("show")
                .WithDescription("Show one provider and its models.");
            providers.AddCommand<ProvidersPresetsCommand>("presets")
                .WithDescription("List built-in provider presets.");
            providers.AddCommand<ProvidersModelsCommand>("models")
                .WithDescription("List models from the catalog (models.dev feed, cached locally).")
                .WithExample("providers", "models", "anthropic", "--refresh");
            providers.AddCommand<ProvidersAddCommand>("add")
                .WithDescription("Add a provider to the user settings.")
                .WithExample("providers", "add", "openai")
                .WithExample("providers", "add", "work", "--preset", "azure", "--endpoint", "https://contoso.openai.azure.com/", "--default-model", "gpt-4.1");
            providers.AddCommand<ProvidersRemoveCommand>("remove")
                .WithAlias("rm")
                .WithDescription("Remove a provider from the user settings.");
        });
}
