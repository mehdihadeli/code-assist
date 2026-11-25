using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodeAssist.Tools.Diagnostics;

public static class RuntimeTelemetry
{
    public const string SourceName = "CodeAssist.Runtime";
    public static ActivitySource Activities { get; } = new(SourceName);
    private static readonly Meter Instruments = new(SourceName);
    private static readonly Counter<long> Operations = Instruments.CreateCounter<long>("codeassist.operation.count", "{operation}");
    private static readonly Histogram<double> Duration = Instruments.CreateHistogram<double>("codeassist.operation.duration", "s");
    private static readonly UpDownCounter<long> Active = Instruments.CreateUpDownCounter<long>("codeassist.operation.active", "{operation}");
    private static readonly Counter<long> Tokens = Instruments.CreateCounter<long>("codeassist.model.token.count", "{token}");
    public static event Action<OperationRecord>? Completed;

    public static OperationScope Start(RuntimeOperation operation, ActivityKind kind = ActivityKind.Internal) => new(operation, kind);

    public static void RecordTokens(long? input, long? output)
    {
        if (input is >= 0) Tokens.Add(input.Value, new KeyValuePair<string, object?>("direction", "input"));
        if (output is >= 0) Tokens.Add(output.Value, new KeyValuePair<string, object?>("direction", "output"));
    }

    public sealed class OperationScope : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string _operation;
        private readonly Activity? _activity;
        private string _outcome = "success";
        private string? _error;
        private int _disposed;

        internal OperationScope(RuntimeOperation operation, ActivityKind kind)
        {
            _operation = operation.ToString().ToLowerInvariant();
            _activity = Activities.StartActivity($"codeassist.{_operation}", kind);
            _activity?.SetTag("codeassist.operation", _operation);
            Active.Add(1, new KeyValuePair<string, object?>("operation", _operation));
        }

        public void Fail(Exception exception)
        {
            _outcome = exception is OperationCanceledException ? "canceled" : "error";
            _error = exception.GetType().Name;
            _activity?.SetTag("error.type", _error);
            if (_outcome == "error") _activity?.SetStatus(ActivityStatusCode.Error);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
            var tags = new TagList { { "operation", _operation }, { "outcome", _outcome } };
            Operations.Add(1, tags);
            Duration.Record(elapsed, tags);
            Active.Add(-1, new KeyValuePair<string, object?>("operation", _operation));
            _activity?.SetTag("codeassist.outcome", _outcome);
            var record = new OperationRecord(_operation, _outcome, elapsed, _error);
            foreach (var callback in Completed?.GetInvocationList() ?? [])
                try { ((Action<OperationRecord>)callback)(record); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            _activity?.Dispose();
        }
    }
}

public enum RuntimeOperation { Turn, Model, Tool, Connector }
public sealed record OperationRecord(string Operation, string Outcome, double DurationSeconds, string? ErrorType);