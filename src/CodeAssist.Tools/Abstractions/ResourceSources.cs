namespace CodeAssist.Tools;

public sealed class ResourceSources : IResourceSource
{
    private readonly Dictionary<string, IResourceSource> _sources = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Servers => _sources.Keys.Order(StringComparer.Ordinal).ToArray();

    public ResourceSources(IEnumerable<IResourceSource> sources)
    {
        foreach (var source in sources)
            foreach (var server in source.Servers)
                if (!_sources.TryAdd(server, source)) throw new ArgumentException($"Resource server '{server}' is registered twice.", nameof(sources));
    }

    public Task<ResourceCatalog> ListAsync(string server, ToolContext context, CancellationToken cancellationToken) => Source(server).ListAsync(server, context, cancellationToken);
    public Task<IReadOnlyList<ResourceContent>?> ReadAsync(string server, string uri, ToolContext context, CancellationToken cancellationToken) => Source(server).ReadAsync(server, uri, context, cancellationToken);
    private IResourceSource Source(string server) => _sources.TryGetValue(server, out var source) ? source : throw new ArgumentException("Unknown resource server.", nameof(server));
}