namespace CodeAssist.Models.Chat.Providers;

public sealed class DeepSeekProvider() : OpenAiCompatibleProvider("deepseek", "DeepSeek", "https://api.deepseek.com/v1/", ["DEEPSEEK_API_KEY"]);
