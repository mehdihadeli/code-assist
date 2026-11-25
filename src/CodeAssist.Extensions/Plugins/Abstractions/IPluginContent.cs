using CodeAssist.Contracts;

namespace CodeAssist.Extensions.Plugins;

public interface IPluginContent
{
    IReadOnlyList<PluginCommandDto> Commands { get; }
    string ExpandPrompt(string name, string input);
}