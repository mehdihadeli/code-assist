namespace CodeAssist.Models.Chat.Providers;

public sealed class GroqProvider() : OpenAiCompatibleProvider("groq", "Groq", "https://api.groq.com/openai/v1/", ["GROQ_API_KEY"]);
