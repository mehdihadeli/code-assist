using System.Text;
using System.Text.Json;
using CodeAssist.Contracts;
using CodeAssist.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CodeAssist.Sessions;

public sealed class AgentSessionService : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Session> _sessions = [];
    private readonly Func<string, IChatClient> _clients;
    private readonly AgentSessionLimits _limits;
    private readonly string _instructions;
    private readonly Func<Guid, string, IReadOnlyList<AITool>>? _tools;
    private readonly Func<string, bool>? _hostedExecution;
    private readonly CancellationTokenSource _shutdown = new();
    private int _creating;
    private int _running;
    private bool _disposed;
    private IConversationStore? _store;

    public AgentSessionService(Func<string, IChatClient> clients, AgentSessionLimits? limits = null, string? instructions = null,
        Func<Guid, string, IReadOnlyList<AITool>>? tools = null, Func<string, bool>? hostedExecution = null)
    {
        _clients = clients;
        _tools = tools;
        _hostedExecution = hostedExecution;
        _limits = limits ?? new();
        _instructions = instructions ?? "You are CodeAssist, a coding assistant. Answer accurately. No workspace tools are available in this conversation.";
        if (_limits.MaxSessions < 1 || _limits.MaxConcurrentTurns < 1 || _limits.MaxMessages < 2 ||
            _limits.MaxInputCharacters < 1 || _limits.MaxOutputCharacters < 1 || _limits.MaxHistoryCharacters < 1 ||
            _limits.TurnTimeout <= TimeSpan.Zero || _limits.TurnTimeout > TimeSpan.FromDays(1) ||
            _limits.MaxSessionTurns < 1 || _limits.MaxSessionTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public async Task<AgentSessionDto> CreateAsync(string model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 512) throw new ArgumentException("Model reference is too long.", nameof(model));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.Count + _creating >= _limits.MaxSessions) throw new SessionCapacityException("Session capacity reached. Delete an idle session first.");
            _creating++;
        }
        IChatClient? client = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            client = _clients(model);
            var sessionId = Guid.NewGuid();
            var agent = new ChatClientAgent(client, instructions: _instructions, name: "CodeAssist");
            if (_tools is not null && agent.GetService(typeof(FunctionInvokingChatClient)) is FunctionInvokingChatClient invoker)
                invoker.MaximumIterationsPerRequest = 12;
            var state = await agent.CreateSessionAsync(cancellationToken);
            var created = new Session(sessionId, model, client, agent, state, _hostedExecution?.Invoke(model) == true);
            await PersistAsync(created, state, [], cancellationToken);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessions.Add(created.Id, created);
                client = null;
                return Snapshot(created);
            }
        }
        finally
        {
            client?.Dispose();
            lock (_sync) _creating--;
        }
    }

    public IReadOnlyList<AgentSessionDto> List()
    {
        lock (_sync) return _sessions.Values.OrderBy(session => session.CreatedAt).Select(Snapshot).ToArray();
    }

    public AgentSessionDto Get(Guid id)
    {
        lock (_sync) return Snapshot(Find(id));
    }

    public IReadOnlyList<AgentMessageDto> Messages(Guid id)
    {
        lock (_sync) return Find(id).Messages.ToArray();
    }

    public async Task<AgentSessionArchive> ExportAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Session session;
        AgentSession state;
        AgentMessageDto[] messages;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = Find(id);
            if (session.Active is not null || session.Editing) throw new SessionConflictException("Wait for the active operation before exporting or forking.");
            state = session.State;
            messages = session.Messages.ToArray();
        }
        var saved = await session.Agent.SerializeSessionAsync(state, cancellationToken: cancellationToken);
        var archive = new AgentSessionArchive(1, session.Model, session.CreatedAt, messages, saved.Clone());
        ValidateArchive(archive);
        return archive;
    }

    public async Task<AgentSessionDto> ForkAsync(Guid id, CancellationToken cancellationToken = default) =>
        await ImportAsync(await ExportAsync(id, cancellationToken), cancellationToken);

    public async Task<AgentSessionDto> ImportAsync(AgentSessionArchive archive, CancellationToken cancellationToken = default)
    {
        ValidateArchive(archive);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.Count + _creating >= _limits.MaxSessions) throw new SessionCapacityException("Session capacity reached. Delete an idle session first.");
            _creating++;
        }
        IChatClient? client = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            client = _clients(archive.Model);
            var agent = new ChatClientAgent(client, instructions: _instructions, name: "CodeAssist");
            if (_tools is not null && agent.GetService(typeof(FunctionInvokingChatClient)) is FunctionInvokingChatClient invoker)
                invoker.MaximumIterationsPerRequest = 12;
            AgentSession state;
            try { state = await agent.DeserializeSessionAsync(archive.State, cancellationToken: cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ArgumentException("Archive contains invalid framework session state.", nameof(archive));
            }
            var imported = new Session(Guid.NewGuid(), archive.Model, client, agent, state, _hostedExecution?.Invoke(archive.Model) == true);
            imported.Messages.AddRange(archive.Messages);
            imported.Characters = Math.Max(archive.Messages.Sum(message => message.Text.Length), archive.State.GetRawText().Length);
            await PersistAsync(imported, state, imported.Messages, cancellationToken);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessions.Add(imported.Id, imported);
                client = null;
                return Snapshot(imported);
            }
        }
        finally
        {
            client?.Dispose();
            lock (_sync) _creating--;
        }
    }

    private void ValidateArchive(AgentSessionArchive archive, bool transfer = true)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Version != 1 || string.IsNullOrWhiteSpace(archive.Model) || archive.Model.Length > 512 ||
            archive.Messages is null || archive.Messages.Length > _limits.MaxMessages || archive.Messages.Length % 2 != 0 ||
            archive.State.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid session archive.", nameof(archive));
        long characters = 0;
        for (var index = 0; index < archive.Messages.Length; index++)
        {
            var message = archive.Messages[index];
            if (message is null || message.Sequence != index + 1 || message.Role != (index % 2 == 0 ? "user" : "assistant") || message.Text is null ||
                message.Text.Length > (message.Role == "user" ? _limits.MaxInputCharacters : _limits.MaxOutputCharacters))
                throw new ArgumentException("Invalid archive messages.", nameof(archive));
            characters += message.Text.Length;
        }
        if (characters > _limits.MaxHistoryCharacters || archive.State.GetRawText().Length > _limits.MaxHistoryCharacters ||
            transfer && JsonSerializer.SerializeToUtf8Bytes(archive, JsonSerializerOptions.Web).Length > 393216)
            throw new ArgumentException("Archive exceeds the session transfer budget of 384 KiB.", nameof(archive));
    }

    public bool Delete(Guid id)
    {
        Session session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out session!)) return false;
            if (session.Active is not null || session.Editing) throw new SessionConflictException("Wait for the active operation before deleting its session.");
            _store?.RemoveAsync(id, session.Revision, CancellationToken.None).GetAwaiter().GetResult();
            _sessions.Remove(id);
        }
        session.Client.Dispose();
        return true;
    }

    public async Task<bool> CancelAsync(Guid id)
    {
        CancellationTokenSource? active;
        lock (_sync) active = Find(id).Active;
        if (active is null) return false;
        try { await active.CancelAsync(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    public async Task<AgentMessageDto> SendAsync(Guid id, string text,
        Func<AgentTurnEvent, CancellationToken, ValueTask>? publish = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > _limits.MaxInputCharacters) throw new ArgumentException("Message exceeds the input limit.", nameof(text));
        Session session;
        CancellationTokenSource active;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = Find(id);
            if (session.Active is not null || session.Editing) throw new SessionConflictException("A session operation is already running.");
            if (session.Archived) throw new SessionConflictException("Restore this archived session before sending a message.");
            if (session.CompletedTurns + session.FailedTurns >= _limits.MaxSessionTurns ||
                _limits.MaxSessionTokens is { } budget && (session.InputTokens + session.OutputTokens >= budget || session.CompletedTurns + session.FailedTurns > 0 && !session.TokensReported))
                throw new SessionConflictException("Session turn or token budget reached; usage must be reported when a token budget is configured.");
            if (session.Messages.Count + 2 > _limits.MaxMessages || session.Characters + text.Length > _limits.MaxHistoryCharacters)
                throw new SessionConflictException("Conversation limit reached. Start a new session.");
            if (_running >= _limits.MaxConcurrentTurns) throw new SessionCapacityException("Concurrent turn capacity reached.");
            active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            active.CancelAfter(_limits.TurnTimeout);
            session.Active = active;
            _running++;
        }
        var turnId = Guid.NewGuid();
        var sequence = 0;
        var token = active.Token;
        long inputTokens = 0;
        long outputTokens = 0;
        var reported = false;
        var accounted = false;
        var committed = false;
        try
        {
            var saved = await session.Agent.SerializeSessionAsync(session.State, cancellationToken: token);
            var working = await session.Agent.DeserializeSessionAsync(saved, cancellationToken: token);
            if (publish is not null) await publish(new(turnId, ++sequence, "turn.started", null), token);
            var answer = new StringBuilder();
            long generatedCharacters = 0;
            var options = _tools is null ? null : new ChatClientAgentRunOptions
            {
                ChatOptions = new() { Tools = _tools(id, session.Model).ToList() },
            };
            await foreach (var update in session.Agent.RunStreamingAsync(text, working, options, cancellationToken: token))
            {
                token.ThrowIfCancellationRequested();
                foreach (var usage in update.Contents.OfType<UsageContent>())
                {
                    inputTokens = checked(inputTokens + Math.Max(0, usage.Details.InputTokenCount ?? 0));
                    outputTokens = checked(outputTokens + Math.Max(0, usage.Details.OutputTokenCount ?? 0));
                    reported |= usage.Details.InputTokenCount.HasValue && usage.Details.OutputTokenCount.HasValue;
                }
                foreach (var content in update.Contents)
                    generatedCharacters += content switch
                    {
                        TextContent visible => visible.Text.Length,
                        TextReasoningContent reasoning => reasoning.Text.Length,
                        UsageContent => 0,
                        McpServerToolCallContent remoteCall when session.AllowsHosted => System.Text.Json.JsonSerializer.Serialize(remoteCall).Length,
                        McpServerToolResultContent remoteResult when session.AllowsHosted => System.Text.Json.JsonSerializer.Serialize(remoteResult).Length,
                        AIContent metadata when session.AllowsHosted && metadata.GetType() == typeof(AIContent) && metadata.AdditionalProperties?.ContainsKey("codeassist.hosted.block") == true => System.Text.Json.JsonSerializer.Serialize(metadata).Length,
                        FunctionCallContent call when _tools is not null => System.Text.Json.JsonSerializer.Serialize(call.Arguments).Length,
                        FunctionResultContent toolResult when _tools is not null => System.Text.Json.JsonSerializer.Serialize(toolResult.Result).Length,
                        _ => throw new NotSupportedException("This session API supports text and reasoning content only."),
                    };
                if (generatedCharacters > _limits.MaxOutputCharacters ||
                    session.Characters + (long)text.Length + generatedCharacters > _limits.MaxHistoryCharacters)
                    throw new SessionConflictException("Response exceeds the conversation limit.");
                if (publish is not null)
                    foreach (var content in update.Contents.Where(content => content is McpServerToolCallContent or McpServerToolResultContent))
                        await publish(new(turnId, ++sequence, content is McpServerToolCallContent ? "hosted.call" : "hosted.result", System.Text.Json.JsonSerializer.Serialize(content)), token);
                var delta = update.Text;
                if (delta.Length == 0) continue;
                answer.Append(delta);
                if (publish is not null) await publish(new(turnId, ++sequence, "text.delta", delta), token);
            }
            token.ThrowIfCancellationRequested();
            var messages = session.Messages.ToList();
            messages.Add(new(messages.Count + 1, "user", text, DateTimeOffset.UtcNow));
            var result = new AgentMessageDto(messages.Count + 1, "assistant", answer.ToString(), DateTimeOffset.UtcNow);
            messages.Add(result);
            session.InputTokens = checked(session.InputTokens + inputTokens);
            session.OutputTokens = checked(session.OutputTokens + outputTokens);
            session.TokensReported = session.CompletedTurns == 0 ? reported : session.TokensReported && reported;
            session.CompletedTurns++;
            accounted = true;
            await PersistAsync(session, working, messages, token);
            lock (_sync)
            {
                session.State = working;
                session.Messages.Clear();
                session.Messages.AddRange(messages);
                session.Characters += checked(text.Length + (int)generatedCharacters);
            }
            committed = true;
            if (publish is not null) await publish(new(turnId, ++sequence, "turn.completed", null), token);
            return result;
        }
        catch
        {
            if (!committed)
            {
                if (accounted) session.CompletedTurns--;
                else
                {
                    session.InputTokens = checked(session.InputTokens + inputTokens);
                    session.OutputTokens = checked(session.OutputTokens + outputTokens);
                    session.TokensReported = session.CompletedTurns + session.FailedTurns == 0 ? reported : session.TokensReported && reported;
                }
                session.FailedTurns++;
                if (!accounted && _store is not null)
                    await PersistAsync(session, session.State, session.Messages, CancellationToken.None);
            }
            throw;
        }
        finally
        {
            lock (_sync)
            {
                session.Active = null;
                _running--;
                if (_disposed) session.Client.Dispose();
            }
            active.Dispose();
        }
    }

    public async Task EnablePersistenceAsync(IConversationStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_store is not null || _sessions.Count != 0 || _creating != 0) throw new InvalidOperationException("Persistence must be initialized before creating sessions.");
        }
        var saved = await store.ListAsync(cancellationToken);
        if (saved.Count > _limits.MaxSessions) throw new SessionCapacityException("Stored sessions exceed configured capacity.");
        var restored = new List<Session>();
        try
        {
            foreach (var checkpoint in saved)
            {
                ValidateArchive(checkpoint.Archive, transfer: false);
                if (checkpoint.InputTokens < 0 || checkpoint.OutputTokens < 0 || checkpoint.CompletedTurns < 0 || checkpoint.FailedTurns < 0 || checkpoint.Title is null || checkpoint.Title.Length > 240)
                    throw new IOException("Stored session metadata is invalid.");
                IChatClient client;
                try { client = _clients(checkpoint.Archive.Model); }
                catch (KeyNotFoundException) { continue; }
                try
                {
                    var agent = new ChatClientAgent(client, instructions: _instructions, name: "CodeAssist");
                    if (_tools is not null && agent.GetService(typeof(FunctionInvokingChatClient)) is FunctionInvokingChatClient invoker)
                        invoker.MaximumIterationsPerRequest = 12;
                    var state = await agent.DeserializeSessionAsync(checkpoint.Archive.State, cancellationToken: cancellationToken);
                    var session = new Session(checkpoint.Id, checkpoint.Archive.Model, client, agent, state, _hostedExecution?.Invoke(checkpoint.Archive.Model) == true, checkpoint.Archive.CreatedAt)
                    {
                        Revision = checkpoint.Revision, Title = checkpoint.Title, Archived = checkpoint.Archived,
                        InputTokens = checkpoint.InputTokens, OutputTokens = checkpoint.OutputTokens,
                        CompletedTurns = checkpoint.CompletedTurns, FailedTurns = checkpoint.FailedTurns, TokensReported = checkpoint.TokensReported,
                        Characters = Math.Max(checkpoint.Archive.Messages.Sum(message => message.Text.Length), checkpoint.Archive.State.GetRawText().Length),
                    };
                    session.Messages.AddRange(checkpoint.Archive.Messages);
                    restored.Add(session);
                }
                catch { client.Dispose(); throw; }
            }
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_sessions.Count != 0 || _creating != 0 || _store is not null) throw new InvalidOperationException("Sessions changed during recovery.");
                foreach (var session in restored) _sessions.Add(session.Id, session);
                _store = store;
            }
        }
        catch { foreach (var session in restored) session.Client.Dispose(); throw; }
    }

    public AgentSessionDetails Details(Guid id)
    {
        lock (_sync)
        {
            var session = Find(id);
            return new(id, session.Revision, session.Title, session.Archived, session.InputTokens, session.OutputTokens,
                session.CompletedTurns, session.FailedTurns, session.TokensReported);
        }
    }

    public async Task<AgentSessionDetails> UpdateAsync(Guid id, UpdateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 240 || request.Title.Any(char.IsControl)) throw new ArgumentException("Title must contain 1 to 240 printable characters.");
        Session session;
        lock (_sync)
        {
            session = Find(id);
            if (session.Active is not null || session.Editing) throw new SessionConflictException("Session is busy.");
            session.Editing = true;
        }
        var previousTitle = session.Title;
        var previousArchived = session.Archived;
        try
        {
            session.Title = request.Title;
            session.Archived = request.Archived;
            await PersistAsync(session, session.State, session.Messages, cancellationToken);
            return Details(id);
        }
        catch { session.Title = previousTitle; session.Archived = previousArchived; throw; }
        finally { lock (_sync) session.Editing = false; }
    }

    public Task<IReadOnlyList<long>> CheckpointsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_sync) _ = Find(id);
        return _store?.RevisionsAsync(id, cancellationToken) ?? throw new NotSupportedException("Durable checkpoints are not enabled.");
    }

    public async Task<AgentSessionDto> RewindAsync(Guid id, long revision, long expectedRevision, CancellationToken cancellationToken = default)
    {
        Session session;
        lock (_sync)
        {
            session = Find(id);
            if (session.Active is not null || session.Editing || session.Revision != expectedRevision) throw new SessionConflictException("Session is busy or revision changed.");
            session.Editing = true;
        }
        try
        {
            var checkpoint = await (_store ?? throw new NotSupportedException("Durable checkpoints are not enabled.")).ReadAsync(id, revision, cancellationToken);
            if (checkpoint.Archive.Model != session.Model) throw new SessionConflictException("Checkpoint model differs from this session.");
            ValidateArchive(checkpoint.Archive, transfer: false);
            var state = await session.Agent.DeserializeSessionAsync(checkpoint.Archive.State, cancellationToken: cancellationToken);
            await PersistAsync(session, state, checkpoint.Archive.Messages, cancellationToken);
            lock (_sync)
            {
                session.State = state;
                session.Messages.Clear();
                session.Messages.AddRange(checkpoint.Archive.Messages);
                session.Characters = Math.Max(session.Messages.Sum(message => message.Text.Length), checkpoint.Archive.State.GetRawText().Length);
            }
            return Snapshot(session);
        }
        finally { lock (_sync) session.Editing = false; }
    }

    private async Task PersistAsync(Session session, AgentSession state, IReadOnlyList<AgentMessageDto> messages, CancellationToken cancellationToken)
    {
        if (_store is null) { cancellationToken.ThrowIfCancellationRequested(); return; }
        var serialized = await session.Agent.SerializeSessionAsync(state, cancellationToken: cancellationToken);
        var archive = new AgentSessionArchive(1, session.Model, session.CreatedAt, messages.ToArray(), serialized.Clone());
        var committed = await _store.CommitAsync(new(session.Id, session.Revision, archive, session.Title, session.Archived,
            session.InputTokens, session.OutputTokens, session.CompletedTurns, session.FailedTurns, session.TokensReported), session.Revision, cancellationToken);
        session.Revision = committed.Revision;
    }

    public void Dispose()
    {
        Session[] idle;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            idle = _sessions.Values.Where(session => session.Active is null).ToArray();
            _sessions.Clear();
        }
        _shutdown.Cancel();
        _shutdown.Dispose();
        foreach (var session in idle) session.Client.Dispose();
    }

    private Session Find(Guid id) => _sessions.TryGetValue(id, out var session) ? session : throw new KeyNotFoundException("Session not found.");
    private static AgentSessionDto Snapshot(Session session) => new(session.Id, session.Model, session.CreatedAt, session.Active is not null, session.Messages.Count);

    private sealed class Session(Guid id, string model, IChatClient client, ChatClientAgent agent, AgentSession state, bool allowsHosted, DateTimeOffset? createdAt = null)
    {
        public bool AllowsHosted { get; } = allowsHosted;
        public Guid Id { get; } = id;
        public string Model { get; } = model;
        public DateTimeOffset CreatedAt { get; } = createdAt ?? DateTimeOffset.UtcNow;
        public IChatClient Client { get; } = client;
        public ChatClientAgent Agent { get; } = agent;
        public AgentSession State { get; set; } = state;
        public List<AgentMessageDto> Messages { get; } = [];
        public int Characters { get; set; }
        public CancellationTokenSource? Active { get; set; }
        public bool Editing { get; set; }
        public long Revision { get; set; }
        public string Title { get; set; } = "Untitled conversation";
        public bool Archived { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CompletedTurns { get; set; }
        public long FailedTurns { get; set; }
        public bool TokensReported { get; set; }
    }
}