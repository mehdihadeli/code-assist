using CodeAssist.Mcp;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodeAssist.Server.Http;

internal static class ConnectorEndpoints
{
    internal static void MapConnectors(this WebApplication app, McpControlPorts? ports)
    {
        var routes = app.MapGroup("/v1/sessions/{id:guid}/connectors").WithTags("Connectors");
        routes.MapGet("/", async Task<Results<Ok<IReadOnlyList<McpConnectionStatus>>, ProblemHttpResult>> (Guid id, AgentSessionService sessions, CancellationToken token) =>
            await Attempt<IReadOnlyList<McpConnectionStatus>>(async () =>
            {
                _ = sessions.Get(id);
                if (ports is null) return [];
                var result = new List<McpConnectionStatus>();
                foreach (var server in ports.Connections.Servers) result.Add(await ports.Connections.StatusAsync(server, ports.Context(id), token));
                return result;
            })).WithName("ListConnectors").WithSummary("Inspect configured connectors for one existing conversation without opening connections.");
        routes.MapGet("/{server}/connection", async Task<Results<Ok<McpConnectionStatus>, ProblemHttpResult>> (Guid id, string server, AgentSessionService sessions, CancellationToken token) =>
            await Attempt(async () => await Require(id, server, sessions).Connections.StatusAsync(server, ports!.Context(id), token)))
            .WithName("GetConnectorConnection").WithSummary("Inspect one session-owned connector.");
        routes.MapPost("/{server}/connection", async Task<Results<Created<McpConnectionStatus>, ProblemHttpResult>> (Guid id, string server, CreateConnectorConnectionRequest request, AgentSessionService sessions, CancellationToken token) =>
        {
            if (!request.Confirmed) return TypedResults.Problem(statusCode: 403, detail: "Explicit connector startup consent is required.");
            var result = await Attempt(async () => await Require(id, server, sessions, idle: true).Connections.ConnectAsync(server, ports!.Context(id), token));
            if (result.Result is ProblemHttpResult problem) return problem;
            return TypedResults.Created($"/v1/sessions/{id}/connectors/{Uri.EscapeDataString(server)}/connection", ((Ok<McpConnectionStatus>)result.Result).Value!);
        }).WithName("CreateConnectorConnection").WithSummary("Explicitly start a trusted configured connector; may launch a child process or OAuth flow.");
        routes.MapDelete("/{server}/connection", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, string server, AgentSessionService sessions, CancellationToken token) =>
        {
            var result = await Attempt(async () => { await Require(id, server, sessions, idle: true).Connections.DisconnectAsync(server, ports!.Context(id), token); return true; });
            return result.Result is ProblemHttpResult problem ? problem : TypedResults.NoContent();
        }).WithName("DeleteConnectorConnection").WithSummary("Close this conversation's connector without removing persisted credentials.");
        routes.MapDelete("/{server}/credentials", async Task<Results<NoContent, ProblemHttpResult>> (Guid id, string server, AgentSessionService sessions, CancellationToken token) =>
        {
            var result = await Attempt(async () => { await Require(id, server, sessions, idle: true).Connections.ForgetCredentialsAsync(server, ports!.Context(id), token); return true; });
            return result.Result is ProblemHttpResult problem ? problem : TypedResults.NoContent();
        }).WithName("ForgetConnectorCredentials").WithSummary("Close this session's connector and remove locally owned credentials; does not revoke them at the issuer.");
        routes.MapGet("/{server}/prompts", async Task<Results<Ok<IReadOnlyList<McpPromptEntry>>, ProblemHttpResult>> (Guid id, string server, AgentSessionService sessions, CancellationToken token) =>
            await Attempt(async () => await (Require(id, server, sessions).Prompts ?? throw new NotSupportedException()).ListPromptsAsync(server, ports!.Context(id), token)))
            .WithName("ListConnectorPrompts").WithSummary("List untrusted prompt metadata from an already connected server.");
        routes.MapPost("/{server}/prompts/resolve", async Task<Results<Ok<McpPromptResult>, ProblemHttpResult>> (Guid id, string server, ResolveConnectorPromptRequest request, AgentSessionService sessions, CancellationToken token) =>
            await Attempt(async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.Arguments?.Count > 32 || request.Arguments?.Any(pair => pair.Key.Length > 128 || pair.Value is null || pair.Value.Length > 8000) == true)
                    throw new ArgumentException("Invalid prompt invocation.");
                return await (Require(id, server, sessions, idle: true).Prompts ?? throw new NotSupportedException()).GetPromptAsync(server, request.Name, request.Arguments ?? new Dictionary<string, string>(), ports!.Context(id), token);
            })).WithName("ResolveConnectorPrompt").WithSummary("Retrieve untrusted prompt messages without promoting roles, invoking tools or calling a model.");

        McpControlPorts Require(Guid id, string server, AgentSessionService sessions, bool idle = false)
        {
            var session = sessions.Get(id);
            if (idle && session.Running) throw new SessionConflictException("Wait for the current turn before changing connector state.");
            if (ports is null || !ports.Connections.Servers.Contains(server, StringComparer.Ordinal)) throw new KeyNotFoundException();
            return ports;
        }
    }

    private static async Task<Results<Ok<TResult>, ProblemHttpResult>> Attempt<TResult>(Func<Task<TResult>> action)
    {
        try { return TypedResults.Ok(await action()); }
        catch (UnauthorizedAccessException) { return TypedResults.Problem(statusCode: 403, detail: "Connector access is restricted."); }
        catch (InvalidOperationException) { return TypedResults.Problem(statusCode: 409, detail: "Connector is disconnected or its session is closing."); }
        catch (IOException) { return TypedResults.Problem(statusCode: 502, detail: "Connector operation failed; trusted local diagnostics may provide details."); }
    }
}