using System.ComponentModel;
using CodeAssist.Cli.Hosting;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Skills;

public sealed class SkillCommandSettings : OutputSettings
{
    [CommandArgument(0, "[ID]"), Description("Short or origin-qualified skill id.")]
    public string? Id { get; init; }
    [CommandOption("--agent"), Description("Inspect agent visibility rather than human invocation.")]
    public bool Agent { get; init; }
    [CommandOption("--input <TEXT>"), Description("Untrusted invocation input; no shell expansion.")]
    public string Input { get; init; } = "";
    [CommandOption("--arg <VALUE>"), Description("Repeatable positional argument; order is preserved.")]
    public string[] Arguments { get; init; } = [];
    [CommandOption("--file <PATH>"), Description("Explicit workspace-relative activity path for agent focus matching.")]
    public string[] Files { get; init; } = [];
}

public sealed class SkillCommand(CliContext cli) : CliCommand<SkillCommandSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, SkillCommandSettings options, CancellationToken cancellation)
    {
        return await Cli.ManageAsync("skills", context.Name, options, cancellation);
    }
}
