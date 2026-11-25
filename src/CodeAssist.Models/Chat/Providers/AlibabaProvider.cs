namespace CodeAssist.Models.Chat.Providers;

public sealed class AlibabaProvider() : OpenAiCompatibleProvider(
    "alibaba", "Alibaba (DashScope)", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/", ["DASHSCOPE_API_KEY", "ALIBABA_API_KEY"]);
