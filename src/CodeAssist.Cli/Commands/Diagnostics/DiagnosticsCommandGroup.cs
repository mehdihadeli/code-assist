using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Diagnostics;

public sealed class DiagnosticsCommandGroup : ICommandGroup
{
    public int Order => 90;

    public void Register(IConfigurator configurator)
    {
        configurator.AddCommand<DoctorCommand>("doctor")
            .WithDescription("Check settings, providers and connectors.");
        configurator.AddCommand<InspectCommand>("inspect")
            .WithDescription("Print local paths or role metadata without inference or secret values.");
    }
}
