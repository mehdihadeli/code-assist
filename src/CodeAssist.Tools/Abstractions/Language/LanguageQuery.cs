namespace CodeAssist.Tools;

public sealed record LanguageQuery(string Operation, string Path, string Language, string Text, int Line, int Column, string? Search);