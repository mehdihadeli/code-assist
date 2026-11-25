using CodeAssist.Contracts;
using CodeAssist.Tools;

namespace CodeAssist.Security;

public sealed class ToolApprovalBroker(Func<ToolSpec, ToolPlan, ToolContext, ToolAuthorization?> policy) : IToolAuthorization
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Pending> _pending = [];
    private sealed record Pending(ToolApprovalRequest Request, TaskCompletionSource<bool> Completion);

    public IReadOnlyList<ToolApprovalRequest> List(Guid sessionId)
    {
        lock (_sync) return _pending.Values.Where(item => item.Request.SessionId == sessionId)
            .Select(item => item.Request).OrderBy(item => item.CreatedAt).ToArray();
    }

    public bool Resolve(Guid sessionId, Guid approvalId, bool allowed)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(approvalId, out var item) || item.Request.SessionId != sessionId) return false;
            return item.Completion.TrySetResult(allowed);
        }
    }

    public async Task<ToolAuthorization> AuthorizeAsync(ToolSpec tool, ToolPlan plan, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Effects.Any(effect => effect.Protected)) return new(false, "Protected effects cannot be approved.");
        if (plan.Effects.Count > 128 || plan.Preview?.Length > 65536 || plan.Effects.Any(effect => effect.Target.Length > 8192))
            return new(false, "Approval description exceeds its budget.");
        if (policy(tool, plan, context) is { } decision) return decision;
        var request = new ToolApprovalRequest(Guid.NewGuid(), context.SessionId, context.CallId, tool.Name,
            plan.Effects.Select(effect => new ToolApprovalEffect(effect.Kind.ToString(), effect.Target, effect.Outside, effect.Protected)).ToArray(), plan.Preview, DateTimeOffset.UtcNow);
        var pending = new Pending(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_sync)
        {
            if (_pending.Count >= 128 || _pending.Values.Count(item => item.Request.SessionId == context.SessionId) >= 16)
                return new(false, "Approval queue capacity reached.");
            _pending.Add(request.Id, pending);
        }
        try
        {
            var allowed = await pending.Completion.Task.WaitAsync(cancellationToken);
            return new(allowed, allowed ? null : "User denied this planned call.");
        }
        finally { lock (_sync) _pending.Remove(request.Id); }
    }
}