namespace CodeAssist.Server.Http;

public sealed record HttpServerOptions
{
    public required string Token { get; init; }
    public string Address { get; init; } = "127.0.0.1:0";
    public string DefaultModel { get; init; } = "default/gpt-4.1";
    public bool AllowCatalogRefresh { get; init; } = true;
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}