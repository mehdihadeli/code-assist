using CodeAssist.Tools;

namespace CodeAssist.Tools;

internal sealed record TeamMailRequest(string Operation, Guid Team, LetterDraft? Draft, Guid Letter, int After, int Limit);