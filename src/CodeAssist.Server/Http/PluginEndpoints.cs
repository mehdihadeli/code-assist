using CodeAssist.Extensions.Plugins;
using CodeAssist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodeAssist.Server.Http;

internal static class PluginEndpoints
{
    internal static void MapPlugins(this WebApplication app, IPluginContent? plugins)
    {
        app.MapGet("/v1/plugins/commands", () => TypedResults.Ok(plugins?.Commands ?? []))
            .WithName("ListPluginCommands").WithTags("Plugins").WithSummary("List enabled, policy-permitted text contributions.");
        app.MapPost("/v1/plugins/expand", Results<Ok<PluginPromptDto>, BadRequest, NotFound, ProblemHttpResult> (ExpandPluginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 177 || request.Input is null || request.Input.Length > 65_536)
                return TypedResults.BadRequest();
            if (plugins is null) return TypedResults.NotFound();
            try { return TypedResults.Ok(new PluginPromptDto(plugins.ExpandPrompt(request.Name, request.Input))); }
            catch (KeyNotFoundException) { return TypedResults.NotFound(); }
            catch (InvalidDataException) { return TypedResults.Problem(statusCode: 422, detail: "Plugin guidance is invalid or exceeds its input budget."); }
        }).WithName("ExpandPluginPrompt").WithTags("Plugins").WithSummary("Expand explicitly selected text without model calls or tool execution.");
    }
}