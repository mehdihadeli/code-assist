namespace CodeAssist.Tools;

// Ports for tools that need the session, the host or a user. Tool implementations are thin shims over these;
// the engine/host supplies the implementations, so every surface sees the same behaviour.

public sealed record QuestionOption(string Label, string? Description = null, bool Recommended = false);

public sealed record Question(string? Header, string Text, IReadOnlyList<QuestionOption> Options, bool Multiple);

public sealed record QuestionAnswer(IReadOnlyList<string> Choices, string? FreeText);

public interface IUserInteraction
{
    /// <summary>Blocks until a surface answers. Null means the user dismissed the questions.</summary>
    Task<IReadOnlyList<QuestionAnswer>?> AskAsync(IReadOnlyList<Question> questions, ToolContext context, CancellationToken cancellationToken);
}

public enum TodoStatus { Pending, InProgress, Completed, Cancelled }

public sealed record TodoItem(string Content, TodoStatus Status);

public interface ITodoStore
{
    Task ReplaceAsync(Guid sessionId, IReadOnlyList<TodoItem> items, CancellationToken cancellationToken);
}

public sealed record DelegationRequest(string Role, string Description, string Prompt, string? Model, Guid? ResumeSessionId);

public sealed record DelegationResult(Guid SessionId, string Text, bool IsError);

public interface IDelegation
{
    /// <summary>Roles that may be started as a child session.</summary>
    IReadOnlyList<string> Roles { get; }

    Task<DelegationResult> RunAsync(DelegationRequest request, ToolContext context, CancellationToken cancellationToken);
}

public sealed record WebSearchHit(string Title, string Url, string Snippet);

public interface IWebSearch
{
    string ProviderId { get; }
    IReadOnlyList<string> ProviderIds => [ProviderId];

    Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, ToolContext context, CancellationToken cancellationToken) => SearchAsync(query, cancellationToken);
}

