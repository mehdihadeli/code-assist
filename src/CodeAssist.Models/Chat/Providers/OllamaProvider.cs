namespace CodeAssist.Models.Chat.Providers;

public sealed class OllamaProvider() : OpenAiCompatibleProvider("ollama", "Ollama (local)", "http://localhost:11434/v1/", [], keyOptional: true);
