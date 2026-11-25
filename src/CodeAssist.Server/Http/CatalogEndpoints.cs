using CodeAssist.Sessions;
using CodeAssist.Contracts.Protocol;
using CodeAssist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodeAssist.Server.Http;

internal static class CatalogEndpoints
{
    public static void MapCatalog(this WebApplication app, HttpServerOptions options)
    {
        app.MapGet(CatalogOperations.ListProviders.Route, (CatalogApi api) => TypedResults.Ok(api.ListProviders())).WithName("ListProviders").WithTags("Catalog");
        app.MapGet(CatalogOperations.ListModels.Route, (string? providerId, CatalogApi api) => TypedResults.Ok(api.ListModels(providerId))).WithName("ListModels").WithTags("Catalog");
        app.MapGet("/v1/catalog/models/{providerId}/{**modelId}", Results<Ok<CatalogModelDto>, NotFound> (string providerId, string modelId, CatalogApi api) =>
            api.GetModel(providerId, modelId) is { } model ? TypedResults.Ok(model) : TypedResults.NotFound()).WithName("GetModel").WithTags("Catalog");
        app.MapPost(CatalogOperations.Refresh.Route, async Task<Results<Ok<CatalogRefreshDto>, ProblemHttpResult>> (CatalogApi api, CancellationToken token) =>
            options.AllowCatalogRefresh ? TypedResults.Ok(await api.RefreshAsync(token)) : TypedResults.Problem(statusCode: 403, detail: "Catalog refresh is disabled by configuration."))
            .WithName("RefreshCatalog").WithTags("Catalog");
    }
}