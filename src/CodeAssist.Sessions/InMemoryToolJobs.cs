using CodeAssist.Tools.Text;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed class InMemoryToolJobs : IToolJobs, IAsyncDisposable
{
    private sealed class Entry(ToolJob snapshot, ToolLimits limits, CancellationToken shutdown, TimeSpan maximumDuration) : IDisposable
    {
        public ToolJob Snapshot { get; set; } = snapshot;
        public ToolLimits Limits { get; } = limits;
        public CancellationTokenSource Stop { get; } = new();
        public CancellationTokenSource Deadline { get; } = new(maximumDuration);
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        public Task Work { get; set; } = Task.CompletedTask;

        public void Dispose()
        {
            Stop.Dispose();
            Deadline.Dispose();
            Lifetime.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _capacity;
    private readonly TimeSpan _maximumDuration;
    private readonly Func<ToolJob, CancellationToken, Task>? _persistCompletion;
    private bool _disposed;

    public InMemoryToolJobs(int capacity = 64, TimeSpan? maximumDuration = null, Func<ToolJob, CancellationToken, Task>? persistCompletion = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _persistCompletion = persistCompletion;
        _maximumDuration = maximumDuration ?? TimeSpan.FromHours(4);
        if (_maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
    }

    public event Action<ToolJob>? Completed;

    public ToolJob Start(string description, Func<CancellationToken, Task<ToolResult>> work, ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.Count >= _capacity)
            {
                var oldest = _entries.Values.Where(entry => entry.Snapshot.Status != ToolJobStatus.Running)
                    .OrderBy(entry => entry.Snapshot.CreatedUtc).FirstOrDefault();
                if (oldest is null) throw new InvalidOperationException("Background job capacity reached; stop an active job first.");
                _entries.Remove(oldest.Snapshot.Id);
                oldest.Dispose();
            }
            var snapshot = new ToolJob(Guid.NewGuid(), context.SessionId, description[..Math.Min(description.Length, 240)], ToolJobStatus.Running, DateTimeOffset.UtcNow);
            var entry = new Entry(snapshot, context.Limits, _shutdown.Token, _maximumDuration);
            _entries.Add(snapshot.Id, entry);
            entry.Work = Task.Run(() => RunAsync(entry, work));
            return snapshot;
        }
    }

    public IReadOnlyList<ToolJob> List(Guid sessionId)
    {
        lock (_gate) return _entries.Values.Select(entry => entry.Snapshot).Where(job => job.SessionId == sessionId).OrderBy(job => job.CreatedUtc).ToArray();
    }

    public ToolJob? Read(Guid sessionId, Guid id)
    {
        lock (_gate) return _entries.TryGetValue(id, out var entry) && entry.Snapshot.SessionId == sessionId ? entry.Snapshot : null;
    }

    public async Task<ToolJob?> StopAsync(Guid sessionId, Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry!) || entry.Snapshot.SessionId != sessionId) return null;
            if (entry.Snapshot.Status != ToolJobStatus.Running) return entry.Snapshot;
        }
        try { await entry.Stop.CancelAsync(); }
        catch (ObjectDisposedException) { }
        await entry.Work.WaitAsync(cancellationToken);
        lock (_gate) return entry.Snapshot;
    }

    private async Task RunAsync(Entry entry, Func<CancellationToken, Task<ToolResult>> work)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(entry.Stop.Token, entry.Deadline.Token, entry.Lifetime.Token);
        ToolResult result;
        ToolJobStatus status;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            result = await work(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            status = result.IsError ? ToolJobStatus.Failed : ToolJobStatus.Completed;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            status = entry.Stop.IsCancellationRequested || entry.Lifetime.IsCancellationRequested ? ToolJobStatus.Cancelled : ToolJobStatus.TimedOut;
            result = ToolResult.Error(status == ToolJobStatus.TimedOut ? "Background job exceeded its lifetime limit." : "Background job cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            status = ToolJobStatus.Failed;
            result = ToolResult.Error($"Background job failed: {exception.Message}");
        }

        var output = OutputBudget.Head(result.Text, entry.Limits.OutputMaxLines, entry.Limits.OutputMaxBytes);
        result = result with { Text = output.Truncated ? $"{output.Text}\n[job output truncated]" : output.Text };
        if (result.Attachments?.Sum(attachment => (long)attachment.Data.Length) > entry.Limits.MaxAttachmentBytes)
        {
            result = ToolResult.Error("Background job result exceeds the attachment byte limit.");
            status = ToolJobStatus.Failed;
        }
        var finished = entry.Snapshot with { Status = status, Result = result };
        if (_persistCompletion is not null)
        {
            using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _persistCompletion(finished, persistence.Token); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                finished = finished with { Status = ToolJobStatus.Failed, Result = ToolResult.Error("Job finished but its durable completion notice could not be saved.") };
            }
        }
        lock (_gate) entry.Snapshot = finished;
        if (Completed is not { } subscribers) return;
        foreach (Action<ToolJob> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(finished); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
        }
        await _shutdown.CancelAsync();
        await Task.WhenAll(entries.Select(entry => entry.Work));
        foreach (var entry in entries) entry.Dispose();
        _shutdown.Dispose();
    }
}