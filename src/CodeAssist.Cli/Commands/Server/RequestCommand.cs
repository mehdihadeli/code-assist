using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CodeAssist.Cli.Commands.Conversation;
using CodeAssist.Cli.Hosting;
using Spectre.Console.Cli;

namespace CodeAssist.Cli.Commands.Server;

public sealed class RequestSettings : ConnectionSettings
{
    [CommandArgument(0, "<METHOD>"), Description("GET, POST, PUT, PATCH, DELETE, HEAD or OPTIONS.")]
    public string Method { get; init; } = "";

    [CommandArgument(1, "<PATH>"), Description("Service-relative /v1/... path, or /openapi/v1.json.")]
    public string Path { get; init; } = "";

    [CommandOption("--data <JSON>"), Description("Explicit JSON body, limited to 384 KiB. May invoke billable or mutating operations.")]
    public string? Data { get; init; }
}

public sealed class RequestCommand(CliContext cli) : CliCommand<RequestSettings>(cli)
{
    protected override async Task<int> RunAsync(CommandContext context, RequestSettings settings, CancellationToken cancellation)
    {
        var method = settings.Method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS")) throw new UsageException("Unsupported HTTP method.");
        if ((!settings.Path.StartsWith("/v1/", StringComparison.Ordinal) && settings.Path != "/openapi/v1.json") ||
            settings.Path.Contains('#') || settings.Path.Contains('\\') || settings.Path.Any(char.IsControl)) throw new UsageException("Use a service-relative API path.");
        JsonElement? body = null;
        if (settings.Data is not null)
        {
            if (method is "GET" or "HEAD") throw new UsageException("GET/HEAD do not accept --data.");
            if (Encoding.UTF8.GetByteCount(settings.Data) > 393216) throw new UsageException("Request body exceeds 384 KiB.");
            try { body = JsonSerializer.Deserialize<JsonElement>(settings.Data); }
            catch (JsonException) { throw new UsageException("--data must contain valid JSON."); }
        }
        await Cli.ConnectAsync(settings.Endpoint, cancellation);
        var response = await Cli.Api.RequestAsync(method, settings.Path, body, cancellation);
        if (Cli.WantsJson(settings.Json)) Cli.WriteJson(response);
        else Cli.Console.Profile.Out.Writer.WriteLine(response.Body);
        return ExitCodes.Success;
    }
}