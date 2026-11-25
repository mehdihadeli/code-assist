namespace CodeAssist.Models.Chat.Providers;

public sealed class DeepInfraProvider() : OpenAiCompatibleProvider(
    "deepinfra", "DeepInfra", "https://api.deepinfra.com/v1/openai/", ["DEEPINFRA_API_KEY"]);
