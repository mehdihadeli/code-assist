using CodeAssist.Testing.Live;
using Microsoft.Extensions.AI;
using Shouldly;

namespace CodeAssist.Models.IntegrationTests.Chat;

/// <summary>
/// Calls enabled chat models from normal settings. Off unless ENABLED_LIVE_TESTS=1 or true.
/// </summary>
public sealed class LiveProviderTests
{
    private static readonly LiveTestConfiguration Config = LiveTestConfiguration.Load();
    private static readonly Lazy<LiveProviderConfiguration> Providers = new(() => LiveProviderConfiguration.Load(Config));

    public static IEnumerable<TheoryDataRow<string, string>> ProviderModels()
    {
        if (!Config.Enabled)
        {
            yield return new("", "") { TestDisplayName = "live providers disabled" };
            yield break;
        }
        var models = Providers.Value.Models;
        if (models.Count == 0) yield return new("", "") { TestDisplayName = "no enabled chat models configured" };
        foreach (var model in models)
            yield return new(model.ProviderId, model.ModelId) { TestDisplayName = $"{model.ProviderId}/{model.ModelId}" };
    }

    [Theory]
    [MemberData(nameof(ProviderModels))]
    public async Task Should_ReturnText_When_ChatModelIsEnabled(string providerId, string modelId)
    {
        using var client = CreateClient(providerId, modelId);

        var response = await client.GetResponseAsync("Reply with the single word OK.", cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(ProviderModels))]
    public async Task Should_StreamText_When_ChatModelIsEnabled(string providerId, string modelId)
    {
        using var client = CreateClient(providerId, modelId);

        var text = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync("Count from 1 to 3.", cancellationToken: TestContext.Current.CancellationToken))
        {
            text.Append(update.Text);
        }

        text.ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(ProviderModels))]
    public async Task Should_RequestToolCall_When_ModelSupportsTools(string providerId, string modelId)
    {
        using var client = CreateClient(providerId, modelId, requiresTools: true);
        var weather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "get_weather", "Gets the weather for a city.");

        var response = await client.GetResponseAsync(
            "What is the weather in Paris? Use the tool.",
            new ChatOptions { Tools = [weather], ToolMode = ChatToolMode.RequireAny },
            TestContext.Current.CancellationToken);

        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ShouldContain(call => call.Name == "get_weather");
    }

    [Theory]
    [MemberData(nameof(ProviderModels))]
    public async Task Should_RetainConversationHistory_When_ChatContinues(string providerId, string modelId)
    {
        using var client = CreateClient(providerId, modelId);
        var code = Guid.NewGuid().ToString("N");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, $"Remember this verification code for my next question: {code}. Reply OK."),
        };
        var first = await client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);
        first.Text.ShouldNotBeNullOrWhiteSpace();
        messages.AddRange(first.Messages);
        messages.Add(new(ChatRole.User, "Return the verification code from my previous message exactly."));

        var response = await client.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldContain(code);
    }

    [Theory]
    [MemberData(nameof(ProviderModels))]
    public async Task Should_DecodeToolArguments_When_ToolCallIsStreamed(string providerId, string modelId)
    {
        using var client = CreateClient(providerId, modelId, requiresTools: true);
        var weather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "get_weather", "Gets the weather for a city.");

        var response = await client.GetStreamingResponseAsync(
            "What is the weather in Paris? Use the tool.",
            new ChatOptions { Tools = [weather], ToolMode = ChatToolMode.RequireAny },
            TestContext.Current.CancellationToken).ToChatResponseAsync(cancellationToken: TestContext.Current.CancellationToken);

        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
        calls.ShouldHaveSingleItem();
        var call = calls.Single();
        call.Name.ShouldBe("get_weather");
        call.CallId.ShouldNotBeNullOrWhiteSpace();
        call.Arguments.ShouldNotBeNull().ShouldContainKey("city");
        call.Arguments!["city"].ShouldNotBeNull().ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [MemberData(nameof(ProviderModels))]
    public async Task Should_UseToolResult_When_ConversationContinuesAfterInvocation(string providerId, string modelId)
    {
        using var client = CreateClient(providerId, modelId, requiresTools: true);
        var code = Guid.NewGuid().ToString("N");
        var tool = AIFunctionFactory.Create(() => code, "get_verification_code", "Gets the current verification code.");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Use the tool to retrieve the verification code, then return its exact value."),
        };
        var requested = await client.GetResponseAsync(messages,
            new ChatOptions { Tools = [tool], ToolMode = ChatToolMode.RequireAny }, TestContext.Current.CancellationToken);
        var calls = requested.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
        calls.ShouldHaveSingleItem();
        var call = calls.Single();
        call.Name.ShouldBe(tool.Name);
        call.CallId.ShouldNotBeNullOrWhiteSpace();
        var result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments), TestContext.Current.CancellationToken);
        result.ShouldNotBeNull().ToString().ShouldBe(code);
        messages.AddRange(requested.Messages);
        messages.Add(new(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));

        var response = await client.GetResponseAsync(messages,
            new ChatOptions { Tools = [tool], ToolMode = ChatToolMode.None }, TestContext.Current.CancellationToken);

        response.Text.ShouldContain(code);
    }

    private static IChatClient CreateClient(string providerId, string modelId, bool requiresTools = false)
    {
        Assert.SkipUnless(Config.Enabled, $"Live tests are off. Set {LiveTestConfiguration.EnabledVariable}=1.");
        Assert.SkipWhen(providerId.Length == 0, "No enabled chat models configured.");
        if (requiresTools)
            Assert.SkipUnless(Providers.Value.Models.Any(model => model.ProviderId == providerId && model.ModelId == modelId && model.Tools), "Model tool capability is disabled in normal settings.");
        return Providers.Value.CreateClient(providerId, modelId);
    }
}
