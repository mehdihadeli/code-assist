namespace CodeAssist.Models.Chat.Providers;

public sealed class VllmProvider() : OpenAiCompatibleProvider("vllm", "vLLM (local)", "http://127.0.0.1:8000/v1/", [], keyOptional: true);
