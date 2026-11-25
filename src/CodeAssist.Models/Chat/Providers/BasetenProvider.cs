namespace CodeAssist.Models.Chat.Providers;

public sealed class BasetenProvider() : OpenAiCompatibleProvider("baseten", "Baseten", "https://inference.baseten.co/v1/", ["BASETEN_API_KEY"]);
