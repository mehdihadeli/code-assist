using System.Net;
using System.Text;
using ModelContextProtocol.Authentication;

namespace CodeAssist.Mcp;

public sealed class LoopbackOAuthCallback(Func<Uri, CancellationToken, Task> openBrowser, bool allowLoopbackAuthorization = false)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext authorization, CancellationToken cancellationToken)
    {
        var redirect = authorization.RedirectUri;
        var address = authorization.AuthorizationUri;
        if (!redirect.IsAbsoluteUri || redirect.Scheme != "http" || !redirect.IsLoopback || redirect.UserInfo.Length != 0 || redirect.Query.Length != 0 || redirect.Fragment.Length != 0 || redirect.AbsolutePath == "/")
            throw new ArgumentException("Browser callback requires a loopback HTTP address with an explicit callback path.");
        if (!address.IsAbsoluteUri || address.UserInfo.Length != 0 || address.Fragment.Length != 0 || address.Scheme != "https" && !(allowLoopbackAuthorization && address.Scheme == "http" && address.IsLoopback))
            throw new ArgumentException("Authorization endpoint must use HTTPS.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect.GetLeftPart(UriPartial.Authority) + "/");
            listener.Start();
            await openBrowser(address, cancellationToken);
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                var uri = context.Request.Url;
                if (context.Request.HttpMethod != "GET" || context.Request.RemoteEndPoint is not { Address: { } remote } || !IPAddress.IsLoopback(remote) ||
                    uri is null || uri.GetLeftPart(UriPartial.Path) != redirect.GetLeftPart(UriPartial.Path) || uri.Query.Length > 8192)
                {
                    await RespondAsync(context.Response, 400, cancellationToken);
                    continue;
                }
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                if (query.GetValues("code") is not { Length: 1 } codes || query.GetValues("state") is not { Length: 1 } states ||
                    query.GetValues("iss") is { Length: > 1 } || query["error"] is not null || string.IsNullOrWhiteSpace(codes[0]) || string.IsNullOrWhiteSpace(states[0]))
                {
                    await RespondAsync(context.Response, 400, cancellationToken);
                    return null;
                }
                await RespondAsync(context.Response, 200, cancellationToken);
                return new() { Code = codes[0], State = states[0], Iss = query["iss"] };
            }
            throw new IOException("OAuth callback received too many invalid requests.");
        }
        finally { _gate.Release(); }
    }

    private static async Task RespondAsync(HttpListenerResponse response, int status, CancellationToken cancellationToken)
    {
        try
        {
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            var bytes = Encoding.ASCII.GetBytes(status == 200 ? "Response received. Return to CodeAssist." : "Invalid authorization callback.");
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
        }
        finally { response.Close(); }
    }
}