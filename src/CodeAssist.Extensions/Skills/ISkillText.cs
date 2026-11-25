using CodeAssist.Tools;
using CodeAssist.Contracts;

namespace CodeAssist.Extensions.Skills;

public interface ISkillText
{
    IReadOnlyList<SkillCommandDto> Commands { get; }
    Task<string?> ExpandAsync(string id, string input, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}