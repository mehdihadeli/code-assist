using CodeAssist.Tools;
using ModelContextProtocol.Authentication;

namespace CodeAssist.Mcp;

public sealed record McpHostServices(
    Func<Uri, CancellationToken, Task>? OpenBrowser = null,
    Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>>? Authorize = null,
    Func<string, ToolContext, ITokenCache?>? TokenCache = null,
    Func<string, ToolContext, CancellationToken, Task>? ClearCredentials = null);