namespace CodeAssist.Models.Chat.Providers;

public sealed class FireworksProvider() : OpenAiCompatibleProvider(
    "fireworks", "Fireworks AI", "https://api.fireworks.ai/inference/v1/", ["FIREWORKS_API_KEY"]);
