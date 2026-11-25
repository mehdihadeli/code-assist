namespace CodeAssist.Models.Chat.Providers;

public sealed class ZaiProvider() : OpenAiCompatibleProvider("zai", "Z.AI", "https://api.z.ai/api/paas/v4/", ["ZAI_API_KEY"]);
