using Microsoft.Extensions.AI;

namespace CodeAssist.Models.Chat;

public sealed class HostedConnectorTool(string serverName, Uri address, IReadOnlyDictionary<string, object?>? properties = null)
    : HostedMcpServerTool(serverName, address, properties)
{
    public override string Name => ServerName;
}