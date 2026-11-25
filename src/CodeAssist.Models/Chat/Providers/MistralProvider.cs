namespace CodeAssist.Models.Chat.Providers;

public sealed class MistralProvider() : OpenAiCompatibleProvider("mistral", "Mistral", "https://api.mistral.ai/v1/", ["MISTRAL_API_KEY"]);
