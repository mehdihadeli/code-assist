using CodeAssist.Tools;

namespace CodeAssist.Extensions.Skills;

public enum SkillAudience { Both, Human, Agent, None }
public enum SkillOriginKind { Local, Plugin, Bundled }

public sealed record SkillSummary(string Id, string Description)
{
    public SkillAudience Audience { get; init; } = SkillAudience.Both;
    public string Origin { get; init; } = "local";
    public SkillOriginKind OriginKind { get; init; }
    public string? Title { get; init; }
    public string? Hint { get; init; }
    public IReadOnlyList<string> Focus { get; init; } = [];
    public IReadOnlyList<string> Parameters { get; init; } = [];
}

public sealed record SkillContent(string Id, string Body, IReadOnlyList<string> Files)
{
    public SkillSummary? Summary { get; init; }
    public string? Digest { get; init; }
    public string? BaseDirectory { get; init; }
    public IReadOnlyList<string> Parameters { get; init; } = [];
    public string? Route { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<SkillContextRequest> Gather { get; init; } = [];
}

public sealed record SkillContextRequest(string Slot, string Tool, string Json);
public enum SkillCaller { Agent, Human }
public sealed record SkillInvocation(SkillCaller Caller = SkillCaller.Agent, string Input = "",
    IReadOnlyList<string>? Arguments = null, IReadOnlyDictionary<string, string>? Values = null, IReadOnlyList<string>? Files = null);

public interface ISkillCatalog
{
    IReadOnlyList<SkillSummary> Index { get; }
    IReadOnlyList<SkillSummary> Describe(ToolContext context, SkillCaller caller = SkillCaller.Agent) => Index
        .Where(item => SkillRendering.Admitted(item, caller)).ToArray();
    Task<SkillContent?> LoadAsync(string id, ToolContext context, CancellationToken cancellationToken);
    async Task<SkillContent?> InvokeAsync(string id, SkillInvocation invocation, ToolContext context, CancellationToken cancellationToken)
    {
        var content = await LoadAsync(id, context, cancellationToken);
        return content is null ? null : SkillRendering.Expand(content, invocation, context);
    }
    Task<string?> ReadFileAsync(string id, string path, ToolContext context, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}