using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands;

/// <summary>A self-contained group of commands. Groups are discovered through DI, so adding one needs no change to the host.</summary>
public interface ICommandGroup
{
    /// <summary>Sort order in help output and registration.</summary>
    int Order { get; }

    void Register(IConfigurator configurator);
}
