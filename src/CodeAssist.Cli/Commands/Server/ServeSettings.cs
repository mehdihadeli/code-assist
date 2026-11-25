using System.ComponentModel;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Server;

public sealed class ServeSettings : GlobalSettings
{
    [CommandOption("--listen <ADDRESS>"), Description("Loopback host:port; defaults to host.listen.http.address.")]
    public string? Address { get; init; }
}