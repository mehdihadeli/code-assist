namespace CodeAssist.Models.Chat.Providers;

public sealed class DigitalOceanProvider() : OpenAiCompatibleProvider(
    "digitalocean", "DigitalOcean", "https://inference.do-ai.run/v1/", ["DIGITALOCEAN_API_KEY", "DO_INFERENCE_API_KEY", "DIGITALOCEAN_ACCESS_TOKEN"]);
