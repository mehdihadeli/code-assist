using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Skills;

public sealed class SkillCommandGroup : ICommandGroup
{
    public int Order => 34;
    public void Register(IConfigurator configurator) => configurator.AddBranch("skills", branch =>
    {
        branch.SetDescription("Inspect and expand scoped reusable guidance without inference or execution.");
        branch.AddCommand<SkillCommand>("list").WithDescription("List skills admitted for human invocation, or --agent for model visibility.");
        branch.AddCommand<SkillCommand>("expand").WithDescription("Expand a skill using structured arguments; starts no tools or model.");
        branch.AddCommand<SkillCommand>("diagnose").WithDescription("Report malformed files and source collisions without changing state.");
    });
}