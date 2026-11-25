using System.Text.Json;
using CodeAssist.Configuration;
using CodeAssist.Contracts;
using CodeAssist.Server.Composition;
using CodeAssist.Server.Management;
using CodeAssist.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodeAssist.Server.Http;

internal static class ManagementEndpoints
{
    public static void MapManagement(this WebApplication app, CodeAssistSettings settings, SettingsLocations locations,
        IReadOnlyDictionary<string, string> environment, McpHostServices? mcp)
    {
        app.MapPost("/v1/tools/query", (ToolQueryRequest request) =>
            TypedResults.Ok(ToolComposition.CreateApi(settings).List(request.Allow, request.Deny)))
            .WithName("QueryTools").WithTags("Tools");
        app.MapPost("/v1/management/{domain}", async (string domain, ManagementCommandRequest request, CancellationToken token) =>
        {
            try
            {
                if (request.Operation is null || request.Operation.Length > 32 || request.Arguments.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Invalid management command.");
                return domain switch
                {
                    "plugins" => await new PluginManagementService(settings, locations).ExecuteAsync(request.Operation,
                        request.Arguments.Deserialize<PluginManagementArguments>(JsonSerializerOptions.Web)!, token),
                    "skills" when request.Operation is "list" or "diagnose" or "expand" => await new SkillManagementService(settings, locations).ExecuteAsync(request.Operation,
                        request.Arguments.Deserialize<SkillManagementArguments>(JsonSerializerOptions.Web)!, token),
                    "connectors" when request.Operation is "connect" or "logout" or "prompts" => await new ConnectorManagementService(settings, locations, environment, mcp).ExecuteAsync(request.Operation,
                        request.Arguments.Deserialize<ConnectorManagementArguments>(JsonSerializerOptions.Web)!, token),
                    _ => throw new ArgumentException("Unknown management operation."),
                };
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException)
            {
                return new ManagementReply(JsonSerializer.SerializeToElement(new { }), exception.Message, 2, HasValue: false);
            }
        }).WithName("ManageRuntime").WithTags("Management");
    }
}