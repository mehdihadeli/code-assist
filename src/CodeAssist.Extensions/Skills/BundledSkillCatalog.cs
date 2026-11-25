using System.Security.Cryptography;
using System.Text;
using CodeAssist.Tools;

namespace CodeAssist.Extensions.Skills;

public sealed record SkillBundle(SkillSummary Summary, string Body, IReadOnlyDictionary<string, string>? Resources = null);

public sealed class BundledSkillCatalog : ISkillCatalog
{
    private readonly Dictionary<string, SkillBundle> _bundles;

    public BundledSkillCatalog(IEnumerable<SkillBundle> bundles)
    {
        _bundles = new(StringComparer.Ordinal);
        foreach (var bundle in bundles.Take(65))
        {
            if (_bundles.Count >= 64 || !SkillListing.ValidId(bundle.Summary.Id) || Encoding.UTF8.GetByteCount(bundle.Body) > 65_536 || bundle.Resources?.Count > 32)
                throw new InvalidDataException("Standard skill bundle exceeds its identity, resource or content bounds.");
            var resources = bundle.Resources?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
            if (resources.Any(pair => Path.IsPathRooted(pair.Key) || pair.Key.Split('/').Any(part => part is ".." or "." or "") || pair.Key.Contains('\\') || pair.Key.Contains(':') || pair.Key.Any(char.IsControl) || Encoding.UTF8.GetByteCount(pair.Value) > 65_536))
                throw new InvalidDataException("Standard skill resources must be bounded portable text files.");
            _bundles.Add(bundle.Summary.Id, bundle with { Summary = bundle.Summary with { OriginKind = SkillOriginKind.Bundled, Focus = bundle.Summary.Focus.ToArray(), Parameters = bundle.Summary.Parameters.ToArray() }, Resources = resources });
        }
    }

    public IReadOnlyList<SkillSummary> Index => _bundles.Values.Where(bundle => SkillRendering.Admitted(bundle.Summary, SkillCaller.Agent)).Select(bundle => bundle.Summary with { Focus = bundle.Summary.Focus.ToArray() }).ToArray();
    public IReadOnlyList<SkillSummary> Describe(ToolContext context, SkillCaller caller = SkillCaller.Agent) => _bundles.Values.Where(bundle => SkillRendering.Admitted(bundle.Summary, caller)).Select(bundle => bundle.Summary with { Focus = bundle.Summary.Focus.ToArray() }).ToArray();

    public Task<SkillContent?> LoadAsync(string id, ToolContext context, CancellationToken cancellationToken) => Read(id, SkillCaller.Agent, context, cancellationToken);

    public async Task<SkillContent?> InvokeAsync(string id, SkillInvocation invocation, ToolContext context, CancellationToken cancellationToken)
    {
        var content = await Read(id, invocation.Caller, context, cancellationToken);
        return content is null ? null : SkillRendering.Expand(content, invocation, context);
    }

    public Task<string?> ReadFileAsync(string id, string path, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bundles.TryGetValue(id, out var bundle)) return Task.FromResult<string?>(null);
        if (!SkillRendering.Admitted(bundle.Summary, SkillCaller.Agent)) throw new UnauthorizedAccessException("Standard skill resources are not available to the agent.");
        var text = bundle.Resources?.GetValueOrDefault(path);
        if (text is not null && Encoding.UTF8.GetByteCount(text) > context.Limits.MaxFileBytes) throw new IOException("Standard skill resource exceeds the call budget.");
        return Task.FromResult(text);
    }

    private Task<SkillContent?> Read(string id, SkillCaller caller, ToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bundles.TryGetValue(id, out var bundle)) return Task.FromResult<SkillContent?>(null);
        if (!SkillRendering.Admitted(bundle.Summary, caller)) throw new UnauthorizedAccessException("Standard skill does not admit this caller.");
        if (Encoding.UTF8.GetByteCount(bundle.Body) > context.Limits.MaxFileBytes) throw new IOException("Standard skill exceeds the call budget.");
        return Task.FromResult<SkillContent?>(new(id, bundle.Body, bundle.Resources!.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            Summary = bundle.Summary with { Focus = bundle.Summary.Focus.ToArray() }, Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bundle.Body))),
        });
    }
}