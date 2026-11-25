using CodeAssist.Persistence;
using CodeAssist.Mcp;
using System.Text.Json;
using CodeAssist.Sessions;
using CodeAssist.Tools;
using CodeAssist.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CodeAssist.Server.Http;

internal static class AgentEndpoints
{
    public static void MapAgents(this WebApplication app, HttpServerOptions options, McpControlPorts? connectors = null)
    {
        var sessions = app.MapGroup("/v1/sessions").WithTags("Sessions");
        sessions.MapGet("/", (AgentSessionService service) => TypedResults.Ok(service.List())).WithName("ListSessions");
        sessions.MapPost("/", async (CreateAgentSessionRequest request, AgentSessionService service, CancellationToken token) =>
        {
            var session = await service.CreateAsync(request.Model ?? options.DefaultModel, token);
            return TypedResults.Created($"/v1/sessions/{session.Id}", session);
        }).WithName("CreateSession");
        sessions.MapPost("/import", async (AgentSessionArchive request, AgentSessionService service, CancellationToken token) =>
        {
            var session = await service.ImportAsync(request, token);
            return TypedResults.Created($"/v1/sessions/{session.Id}", session);
        }).WithName("ImportSession");
        sessions.MapGet("/{id:guid}/archive", async (Guid id, AgentSessionService service, CancellationToken token) =>
            TypedResults.Ok(await service.ExportAsync(id, token))).WithName("ExportSession");
        sessions.MapPost("/{id:guid}/fork", async (Guid id, AgentSessionService service, CancellationToken token) =>
        {
            var session = await service.ForkAsync(id, token);
            return TypedResults.Created($"/v1/sessions/{session.Id}", session);
        }).WithName("ForkSession");
        sessions.MapGet("/{id:guid}", (Guid id, AgentSessionService service) => TypedResults.Ok(service.Get(id))).WithName("GetSession");
        sessions.MapGet("/{id:guid}/details", (Guid id, AgentSessionService service) => TypedResults.Ok(service.Details(id))).WithName("GetSessionDetails");
        sessions.MapPost("/{id:guid}/details", async (Guid id, UpdateSessionRequest request, AgentSessionService service, CancellationToken token) =>
            TypedResults.Ok(await service.UpdateAsync(id, request, token))).WithName("UpdateSessionDetails");
        sessions.MapGet("/{id:guid}/checkpoints", async (Guid id, AgentSessionService service, CancellationToken token) =>
            TypedResults.Ok(await service.CheckpointsAsync(id, token))).WithName("ListSessionCheckpoints");
        sessions.MapPost("/{id:guid}/rewind", async (Guid id, RewindSessionRequest request, AgentSessionService service, CancellationToken token) =>
            TypedResults.Ok(await service.RewindAsync(id, request.Revision, request.ExpectedRevision, token))).WithName("RewindSession");
        sessions.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, AgentSessionService service, CancellationToken token) =>
        {
            if (!service.Delete(id)) return TypedResults.NotFound();
            if (connectors is not null) await connectors.Connections.ReleaseSessionAsync(connectors.Context(id), token);
            return TypedResults.NoContent();
        }).WithName("DeleteSession");
        sessions.MapGet("/{id:guid}/messages", (Guid id, AgentSessionService service) => TypedResults.Ok(service.Messages(id))).WithName("ListMessages");
        sessions.MapPost("/{id:guid}/messages", async (Guid id, SendAgentMessageRequest request, AgentSessionService service, CancellationToken token) =>
            TypedResults.Ok(await service.SendAsync(id, request.Text, cancellationToken: token))).WithName("SendMessage");
        sessions.MapPost("/{id:guid}/cancel", async (Guid id, AgentSessionService service) =>
            TypedResults.Ok(new CancelAgentTurnResponse(await service.CancelAsync(id)))).WithName("CancelTurn");
        sessions.MapPost("/{id:guid}/messages/stream", StreamAsync).WithName("StreamMessage")
            .Produces<AgentTurnEvent>(StatusCodes.Status200OK, contentType: "text/event-stream");
    }

    private static async Task StreamAsync(Guid id, SendAgentMessageRequest request, AgentSessionService service, HttpContext context)
    {
        AgentTurnEvent? last = null;
        try
        {
            await service.SendAsync(id, request.Text, async (item, token) =>
            {
                last = item;
                if (!context.Response.HasStarted)
                {
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.CacheControl = "no-store, no-transform";
                    context.Response.Headers["X-Accel-Buffering"] = "no";
                }
                await WriteAsync(context.Response, item, token);
            }, context.RequestAborted);
        }
        catch (Exception exception) when (context.Response.HasStarted && last is not null)
        {
            if (context.RequestAborted.IsCancellationRequested) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var kind = exception is OperationCanceledException ? "turn.canceled" : "turn.failed";
            await WriteAsync(context.Response, new(last.TurnId, last.Sequence + 1, kind, "Turn did not complete. Read committed messages before retrying."), deadline.Token);
        }
    }

    private static async ValueTask WriteAsync(HttpResponse response, AgentTurnEvent item, CancellationToken token)
    {
        await response.WriteAsync($"event: {item.Kind}\ndata: {JsonSerializer.Serialize(item, JsonSerializerOptions.Web)}\n\n", token);
        await response.Body.FlushAsync(token);
    }
}