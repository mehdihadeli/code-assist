namespace CodeAssist.Models.Chat.Providers;

public sealed class MoonshotProvider() : OpenAiCompatibleProvider(
    "moonshotai", "Moonshot AI", "https://api.moonshot.ai/v1/", ["MOONSHOT_API_KEY", "MOONSHOTAI_API_KEY"]);
