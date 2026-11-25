namespace CodeAssist.Models.Chat.Providers;

public sealed class CerebrasProvider() : OpenAiCompatibleProvider("cerebras", "Cerebras", "https://api.cerebras.ai/v1/", ["CEREBRAS_API_KEY"]);
