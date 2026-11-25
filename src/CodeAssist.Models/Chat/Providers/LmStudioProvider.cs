namespace CodeAssist.Models.Chat.Providers;

public sealed class LmStudioProvider() : OpenAiCompatibleProvider("lmstudio", "LM Studio (local)", "http://127.0.0.1:1234/v1/", [], keyOptional: true);
