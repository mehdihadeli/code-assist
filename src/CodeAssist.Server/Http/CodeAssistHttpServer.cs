using CodeAssist.Mcp;
using CodeAssist.Extensions.Skills;
using System.Text.Json;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Tools;
using CodeAssist.Sessions;
using CodeAssist.Contracts;
using CodeAssist.Server.Auth;
using CodeAssist.Server.Composition;
using CodeAssist.Security.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeAssist.Server.Http;

public static class CodeAssistHttpServer
{
    public static WebApplication Build(HttpServerOptions options, AgentSessionService sessions, CatalogApi catalog,
        Action<WebApplicationBuilder>? configure = null, IPluginContent? plugins = null, ISkillText? skills = null, McpControlPorts? connectors = null)
    {
        if (string.IsNullOrWhiteSpace(options.Token) || options.Token.Length < 32 || options.Token.Length > 512)
            throw new ArgumentException("HTTP authentication requires a token between 32 and 512 characters.", nameof(options));
        if (!Uri.TryCreate($"http://{options.Address}", UriKind.Absolute, out var address) || !address.IsLoopback ||
            address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("HTTP hosting requires a loopback host:port address.", nameof(options));
        foreach (var origin in options.AllowedOrigins)
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length != 0 || origin != uri.GetLeftPart(UriPartial.Authority))
                throw new ArgumentException("Allowed origins must be explicit HTTP origins without paths or credentials.", nameof(options));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(CodeAssistHttpServer).Assembly.GetName().Name, EnvironmentName = "Production" });
        builder.WebHost.UseKestrel(server => server.Limits.MaxRequestBodySize = 524_288);
        builder.WebHost.UseUrls(address.GetLeftPart(UriPartial.Authority));
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer<BearerOpenApiTransformer>());
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(sessions.Dispose);
        app.UseHttpAccess(options.Token, options.AllowedOrigins);
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception exception)
            {
                if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
                var (status, detail) = exception switch
                {
                    SessionConflictException conflict => (409, conflict.Message),
                    CodeAssist.Persistence.JournalConflictException => (409, "Stored session revision changed."),
                    SessionCapacityException capacity => (429, capacity.Message),
                    KeyNotFoundException => (404, "Resource not found."),
                    BadHttpRequestException bad => (bad.StatusCode, "Invalid request body."),
                    ArgumentException or JsonException => (400, "Request or model configuration is invalid."),
                    NotSupportedException => (422, "The requested model capability or protocol is not supported."),
                    InvalidDataException => (422, "Content is invalid or exceeds its budget."),
                    UnauthorizedAccessException => (403, "Operation is restricted by policy."),
                    OperationCanceledException => (504, "The turn was canceled or timed out."),
                    _ => (502, "The operation failed. No provider details are exposed."),
                };
                app.Logger.LogWarning("HTTP operation failed ({ExceptionType}); status {Status}", exception.GetType().Name, status);
                await TypedResults.Problem(statusCode: status, detail: detail, title: "Request failed").ExecuteAsync(context);
            }
        });
        app.MapGet("/v1/health", () => TypedResults.Ok(new ServerHealthDto("code-assist", "1", "ready"))).WithName("Health");
        app.MapAgents(options, connectors);
        app.MapCatalog(options);
        app.MapPlugins(plugins);
        app.MapSkills(skills);
        app.MapConnectors(connectors);
        app.MapCommands(app.Services.GetService<PromptCommandCatalog>() ?? new(new CodeAssist.Configuration.CodeAssistSettings(), skills, plugins));
        app.MapOpenApi();
        return app;
    }
}