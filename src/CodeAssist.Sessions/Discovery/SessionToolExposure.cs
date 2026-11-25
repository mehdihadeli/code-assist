using System.Text;
using CodeAssist.Tools;

namespace CodeAssist.Sessions;

public sealed class SessionToolExposure : IToolExposure
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string Name, int Bytes)[]> _sessions = [];
    private static readonly string[] Initial = ["tool.find", "fs.read", "fs.find", "ask.user", "output.read"];

    public IReadOnlyList<ToolSpec> Select(IReadOnlyList<ToolSpec> allowed, ToolContext context)
    {
        if (context.SessionId == Guid.Empty || !allowed.Any(tool => tool.Name == "tool.find")) return allowed;
        string[] active;
        lock (_gate) active = (_sessions.GetValueOrDefault(context.SessionId) ?? []).Select(entry => entry.Name).ToArray();
        var names = Initial.Concat(active).ToHashSet(StringComparer.Ordinal);
        var result = new List<ToolSpec>();
        var bytes = 0;
        foreach (var tool in allowed.Where(tool => names.Contains(tool.Name)).OrderBy(tool => tool.Name == "tool.find" ? 0 : 1).ThenBy(tool => tool.Name, StringComparer.Ordinal))
        {
            var size = Encoding.UTF8.GetByteCount(tool.InputSchema) + Encoding.UTF8.GetByteCount(tool.Description);
            if (bytes + size > 131072 || result.Count == 32) continue;
            result.Add(tool);
            bytes += size;
        }
        return result;
    }

    public IReadOnlyList<string> Activate(IReadOnlyList<ToolSpec> tools, ToolContext context)
    {
        if (context.SessionId == Guid.Empty) throw new InvalidOperationException("Schema activation requires a session.");
        if (tools.Count > 24 || tools.Sum(tool => (long)Encoding.UTF8.GetByteCount(tool.InputSchema) + Encoding.UTF8.GetByteCount(tool.Description)) > 98304) throw new InvalidOperationException("Activation page exceeds schema budget; request fewer tools.");
        lock (_gate)
        {
            if (!_sessions.ContainsKey(context.SessionId) && _sessions.Count >= 512) _sessions.Remove(_sessions.Keys.First());
            var names = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var next = (_sessions.GetValueOrDefault(context.SessionId) ?? []).Where(entry => !names.Contains(entry.Name))
                .Concat(tools.Select(tool => (Name: tool.Name, Bytes: Encoding.UTF8.GetByteCount(tool.InputSchema) + Encoding.UTF8.GetByteCount(tool.Description)))).TakeLast(24).ToList();
            while (next.Sum(entry => entry.Bytes) > 98304) next.RemoveAt(0);
            _sessions[context.SessionId] = next.ToArray();
            return tools.Select(tool => tool.Name).ToArray();
        }
    }
}