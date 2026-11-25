namespace CodeAssist.Contracts.Protocol;

/// <summary>One protocol operation: the id plus the HTTP method and route each transport binds to.</summary>
public sealed record OperationDescriptor(string Id, string Method, string Route);

/// <summary>Catalog operations as data, shared by the server and clients.</summary>
public static class CatalogOperations
{
    public static readonly OperationDescriptor ListProviders = new("catalog.providers.list", "GET", "/v1/catalog/providers");
    public static readonly OperationDescriptor ListModels = new("catalog.models.list", "GET", "/v1/catalog/models");
    public static readonly OperationDescriptor GetModel = new("catalog.models.get", "GET", "/v1/catalog/models/{providerId}/{modelId}");
    public static readonly OperationDescriptor Refresh = new("catalog.refresh", "POST", "/v1/catalog/refresh");
}
