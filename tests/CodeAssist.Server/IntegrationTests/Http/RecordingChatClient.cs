using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace CodeAssist.Server.IntegrationTests.Http;

internal sealed class RecordingChatClient : IChatClient
{
    public ConcurrentQueue<string[]> UserMessages { get; } = new();
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public UsageDetails? ReportedUsage { get; init; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Tests expect real framework streaming execution.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var users = messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).ToArray();
        UserMessages.Enqueue(users);
        var prompt = users.Last();
        if (prompt == "wait")
        {
            Entered.TrySetResult();
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(cancellationToken);
        }
        var responseId = Guid.NewGuid().ToString("N");
        if (ReportedUsage is not null)
            yield return new(ChatRole.Assistant, [new UsageContent(ReportedUsage)]) { ResponseId = responseId, MessageId = responseId };
        if (prompt == "reason")
        {
            var reasoning = new ChatResponseUpdate { Role = ChatRole.Assistant, ResponseId = responseId, MessageId = responseId };
            reasoning.Contents.Add(new TextReasoningContent("unpublished reasoning beyond the limit"));
            yield return reasoning;
        }
        yield return new(ChatRole.Assistant, "reply:") { ResponseId = responseId, MessageId = responseId };
        if (prompt == "fail") throw new HttpRequestException("SECRET_DO_NOT_LEAK");
        yield return new(ChatRole.Assistant, prompt) { ResponseId = responseId, MessageId = responseId, FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() => Disposed = true;
}