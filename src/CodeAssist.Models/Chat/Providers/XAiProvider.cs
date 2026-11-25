namespace CodeAssist.Models.Chat.Providers;

public sealed class XAiProvider() : OpenAiCompatibleProvider("xai", "xAI", "https://api.x.ai/v1/", ["XAI_API_KEY"]);
