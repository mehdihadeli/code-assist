namespace CodeAssist.Models.Chat.Providers;

public sealed class TogetherAiProvider() : OpenAiCompatibleProvider(
    "togetherai", "Together AI", "https://api.together.xyz/v1/", ["TOGETHER_API_KEY", "TOGETHER_AI_API_KEY"]);
