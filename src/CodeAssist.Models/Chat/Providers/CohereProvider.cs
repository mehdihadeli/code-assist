namespace CodeAssist.Models.Chat.Providers;

/// <summary>Cohere through its OpenAI-compatibility endpoint.</summary>
public sealed class CohereProvider() : OpenAiCompatibleProvider("cohere", "Cohere", "https://api.cohere.ai/compatibility/v1/", ["COHERE_API_KEY"]);
