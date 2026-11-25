using System.Text.Json;
using CodeAssist.Server.Composition;
using CodeAssist.Configuration;
using CodeAssist.Contracts;
using CodeAssist.Tools;
using CodeAssist.Extensions.Plugins;
using CodeAssist.Mcp;

namespace CodeAssist.Server.Management;

public sealed class ConnectorManagementService(CodeAssistSettings settings, SettingsLocations locations,
    IReadOnlyDictionary<string, string> environment, McpHostServices? mcp = null)
{
    public async Task<ManagementReply> ExecuteAsync(string operation, ConnectorManagementArguments options, CancellationToken cancellation)
    {
        ManagementReply? reply = null;
        void WriteJson(object value) => reply = new(JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web), "");

        try
        {
            if (options.Arguments.Length > 32) throw new ArgumentException("At most 32 prompt arguments are allowed.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var argument in options.Arguments)
            {
                var pair = argument.Split('=', 2);
                if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || !values.TryAdd(pair[0], pair[1])) throw new ArgumentException("Prompt arguments require unique NAME=VALUE entries.");
            }
            var context = new ToolContext { Workspace = locations.WorkingDirectory, SessionId = Guid.NewGuid() };
            await using var connections = ConnectorComposition.Create(settings, context.Workspace, environment, host: mcp);
            if (operation == "logout")
            {
                await connections.ForgetCredentialsAsync(options.Name, context, cancellation);
                WriteJson(new { Server = options.Name, CredentialsRemoved = true });
                return reply ?? new(JsonSerializer.SerializeToElement(new { }), "");
            }
            var status = await connections.ConnectAsync(options.Name, context, cancellation);
            object result = operation != "prompts" ? new { Status = status, Tools = await connections.ListAsync(options.Name, context, cancellation) } :
                options.Prompt is null ? await connections.ListPromptsAsync(options.Name, context, cancellation) :
                await connections.GetPromptAsync(options.Name, options.Prompt, values, context, cancellation);
            WriteJson(result);
            return reply ?? new(JsonSerializer.SerializeToElement(new { }), "");
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ArgumentException(exception.Message);
        }
    }
}
