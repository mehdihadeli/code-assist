namespace CodeAssist.Tools;

public sealed record ResourceEntry(string Uri, string Name, string? Description = null, string? MimeType = null);

public sealed record ResourceTemplate(string UriTemplate, string Name, string? Description = null);

public sealed record ResourceCatalog(IReadOnlyList<ResourceEntry> Resources, IReadOnlyList<ResourceTemplate> Templates);

public sealed record ResourceContent(string Uri, string? Text = null, byte[]? Data = null);

public interface IResourceSource
{
    IReadOnlyList<string> Servers { get; }

    Task<ResourceCatalog> ListAsync(string server, ToolContext context, CancellationToken cancellationToken);

    Task<IReadOnlyList<ResourceContent>?> ReadAsync(string server, string uri, ToolContext context, CancellationToken cancellationToken);
}