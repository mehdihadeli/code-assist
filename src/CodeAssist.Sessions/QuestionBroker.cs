using System.Collections.Concurrent;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed record PendingQuestion(Guid Id, Guid SessionId, IReadOnlyList<Question> Questions);

public sealed class QuestionBroker : IUserInteraction
{
    private sealed record Pending(PendingQuestion Request, TaskCompletionSource<IReadOnlyList<QuestionAnswer>?> Completion);
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly SemaphoreSlim _capacity = new(128, 128);
    public event Action<PendingQuestion>? Requested;

    public IReadOnlyList<PendingQuestion> PendingFor(Guid sessionId) => _pending.Values.Where(value => value.Request.SessionId == sessionId).Select(value => value.Request).ToArray();

    public async Task<IReadOnlyList<QuestionAnswer>?> AskAsync(IReadOnlyList<Question> questions, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.SessionId == Guid.Empty || questions.Count is < 1 or > 8) throw new ArgumentException("Questions require a real session and 1-8 questions.");
        var listener = Requested;
        if (listener is null) return null;
        if (!await _capacity.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Too many pending questions.");
        var request = new PendingQuestion(Guid.NewGuid(), context.SessionId, questions);
        var completion = new TaskCompletionSource<IReadOnlyList<QuestionAnswer>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = new(request, completion);
        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            listener(request);
            return await completion.Task;
        }
        finally { _pending.TryRemove(request.Id, out _); _capacity.Release(); }
    }

    public bool Answer(Guid sessionId, Guid requestId, IReadOnlyList<QuestionAnswer>? answers)
    {
        if (!_pending.TryGetValue(requestId, out var pending) || pending.Request.SessionId != sessionId) return false;
        if (answers is not null)
        {
            if (answers.Count != pending.Request.Questions.Count) throw new ArgumentException("Answer count does not match question count.");
            foreach (var (index, answer) in answers.Index())
            {
                var question = pending.Request.Questions[index];
                if ((!question.Multiple && answer.Choices.Count > 1) || answer.Choices.Distinct(StringComparer.Ordinal).Count() != answer.Choices.Count || answer.Choices.Any(choice => !question.Options.Any(option => option.Label == choice)) || answer.FreeText?.Length > 32_000) throw new ArgumentException("Answer contains invalid choices or exceeds its text budget.");
            }
        }
        return pending.Completion.TrySetResult(answers);
    }
}