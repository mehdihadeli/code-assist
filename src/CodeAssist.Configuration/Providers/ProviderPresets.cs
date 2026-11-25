namespace CodeAssist.Configuration;

/// <summary>
/// A starting point for a provider entry. Endpoints, key variables and docs links were checked against the models.dev
/// provider files and the vendor SDK documentation (see artifacts/codeassist-architecture-design.md, section 13).
/// </summary>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    ProviderProtocol Protocol,
    string Endpoint,
    IReadOnlyList<string> KeyVariables,
    string Note,
    string Doc,
    ProviderApi Api = ProviderApi.Auto,
    IReadOnlyDictionary<string, string>? Options = null,
    bool NeedsEndpointEdit = false)
{
    public bool KeyOptional => KeyVariables.Count == 0;

    public ProviderSettings ToSettings()
    {
        var provider = new ProviderSettings { Protocol = Protocol, Endpoint = Endpoint, Api = Api };
        if (Options is not null)
            foreach (var (key, value) in Options) provider.Options[key] = value;
        return provider;
    }
}

public static class ProviderPresets
{
    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new("openai", "OpenAI", ProviderProtocol.OpenAi, "https://api.openai.com/v1/", ["OPENAI_API_KEY"],
            "Hosted OpenAI models; uses the Responses API.", "https://platform.openai.com/docs/models", ProviderApi.Responses),
        new("anthropic", "Anthropic", ProviderProtocol.Anthropic, "https://api.anthropic.com/", ["ANTHROPIC_API_KEY"],
            "Hosted Claude models.", "https://docs.anthropic.com/en/docs/about-claude/models"),
        new("azure", "Azure OpenAI", ProviderProtocol.Azure, "https://your-resource.openai.azure.com/", ["AZURE_API_KEY", "AZURE_OPENAI_API_KEY"],
            "Use your resource endpoint; model keys are deployment names. Without a key, Microsoft Entra sign-in is used.",
            "https://learn.microsoft.com/en-us/azure/ai-services/openai/concepts/models", NeedsEndpointEdit: true),
        new("foundry", "Microsoft Foundry models (OpenAI v1)", ProviderProtocol.OpenAi, "https://your-resource.services.ai.azure.com/openai/v1/", [],
            "Foundry resource through its OpenAI v1 surface; supply the key as Providers__<id>__ApiKey.",
            "https://learn.microsoft.com/en-us/agent-framework/agents/providers/", ProviderApi.Responses, NeedsEndpointEdit: true),
        new("gemini", "Google Gemini (OpenAI-compatible)", ProviderProtocol.OpenAi, "https://generativelanguage.googleapis.com/v1beta/openai/",
            ["GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENERATIVE_AI_API_KEY"],
            "Gemini through its OpenAI-compatible surface.", "https://ai.google.dev/gemini-api/docs/models", ProviderApi.Chat),
        new("google", "Google Gemini", ProviderProtocol.Gemini, "https://generativelanguage.googleapis.com/",
            ["GEMINI_API_KEY", "GOOGLE_GENERATIVE_AI_API_KEY", "GOOGLE_API_KEY"],
            "Native Gemini API through Google's SDK.", "https://ai.google.dev/gemini-api/docs/models"),
        new("google-vertex", "Google Vertex AI", ProviderProtocol.Vertex, "", ["GOOGLE_VERTEX_API_KEY"],
            "Application Default Credentials, or an express-mode API key. Options: project and location.", "https://cloud.google.com/vertex-ai/generative-ai/docs"),
        new("amazon-bedrock", "Amazon Bedrock", ProviderProtocol.Bedrock, "", ["AWS_BEARER_TOKEN_BEDROCK"],
            "AWS credential chain, or a Bedrock API key. Options: region and profile.", "https://docs.aws.amazon.com/bedrock/"),
        new("openrouter", "OpenRouter", ProviderProtocol.OpenAi, "https://openrouter.ai/api/v1/", ["OPENROUTER_API_KEY"],
            "Many hosted models behind one key; model ids contain a slash.", "https://openrouter.ai/models", ProviderApi.Chat),
        new("groq", "Groq", ProviderProtocol.OpenAi, "https://api.groq.com/openai/v1/", ["GROQ_API_KEY"],
            "Fast hosted open models.", "https://console.groq.com/docs/models", ProviderApi.Chat),
        new("mistral", "Mistral", ProviderProtocol.OpenAi, "https://api.mistral.ai/v1/", ["MISTRAL_API_KEY"],
            "Hosted Mistral models.", "https://docs.mistral.ai/getting-started/models/", ProviderApi.Chat),
        new("deepseek", "DeepSeek", ProviderProtocol.OpenAi, "https://api.deepseek.com/", ["DEEPSEEK_API_KEY"],
            "Hosted DeepSeek models (chat completions).", "https://api-docs.deepseek.com/quick_start/pricing", ProviderApi.Chat),
        new("xai", "xAI", ProviderProtocol.OpenAi, "https://api.x.ai/v1/", ["XAI_API_KEY"],
            "Hosted Grok models.", "https://docs.x.ai/docs/models", ProviderApi.Chat),
        new("ollama", "Ollama (local)", ProviderProtocol.OpenAi, "http://localhost:11434/v1/", [],
            "Local models; no key required.", "https://ollama.com/library", ProviderApi.Chat),
        new("lmstudio", "LM Studio (local)", ProviderProtocol.OpenAi, "http://127.0.0.1:1234/v1/", [],
            "Local models served by LM Studio; no key required.", "https://lmstudio.ai/models", ProviderApi.Chat),
    ];

    public static ProviderPreset? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
