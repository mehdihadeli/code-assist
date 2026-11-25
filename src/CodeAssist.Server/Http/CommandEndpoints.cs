using CodeAssist.Contracts;
using CodeAssist.Server.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodeAssist.Server.Http;

internal static class CommandEndpoints
{
    internal static void MapCommands(this WebApplication app, PromptCommandCatalog catalog)
    {
        app.MapGet("/v1/commands", () => TypedResults.Ok(catalog.List()))
            .WithName("ListConversationCommands").WithTags("Commands");
        app.MapPost("/v1/commands/expand", async (ExpandConversationCommandRequest request, CancellationToken cancellationToken) =>
            TypedResults.Ok(await catalog.ExpandAsync(request, cancellationToken)))
            .WithName("ExpandConversationCommand").WithTags("Commands");
    }
}