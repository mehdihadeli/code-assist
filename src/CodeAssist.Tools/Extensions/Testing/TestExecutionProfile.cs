namespace CodeAssist.Tools.Extensions;

public sealed record TestExecutionProfile(string Name, string Executable, IReadOnlyList<string> Arguments, bool MicrosoftTestingPlatform, string? FilterArgument = null);