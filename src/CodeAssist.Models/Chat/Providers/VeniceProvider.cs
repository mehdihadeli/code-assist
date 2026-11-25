namespace CodeAssist.Models.Chat.Providers;

public sealed class VeniceProvider() : OpenAiCompatibleProvider("venice", "Venice AI", "https://api.venice.ai/api/v1/", ["VENICE_API_KEY"]);
