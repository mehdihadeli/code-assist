namespace CodeAssist.Models.Chat.Providers;

/// <summary>Model ids contain a slash (for example anthropic/claude-sonnet-4); pass them through unchanged.</summary>
public sealed class OpenRouterProvider() : OpenAiCompatibleProvider("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/", ["OPENROUTER_API_KEY"]);
