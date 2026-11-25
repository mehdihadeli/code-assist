namespace CodeAssist.Models.Chat.Providers;

public sealed class VercelAiGatewayProvider() : OpenAiCompatibleProvider(
    "vercel", "Vercel AI Gateway", "https://ai-gateway.vercel.sh/v1/", ["AI_GATEWAY_API_KEY"]);
