using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace CodeAssist.Security.Http;

public static class HttpAccess
{
    public static void UseHttpAccess(this WebApplication app, string token, IReadOnlyList<string> allowedOrigins)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var origins = allowedOrigins.ToHashSet(StringComparer.Ordinal);
        app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host.Trim('[', ']');
            if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) ||
                context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0)
            {
                if (!origins.Contains(origin) && origin != $"{context.Request.Scheme}://{context.Request.Host}")
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
            }
            if (HttpMethods.IsOptions(context.Request.Method) && origin.Length > 0)
            {
                var method = context.Request.Headers.AccessControlRequestMethod.ToString();
                var headers = context.Request.Headers.AccessControlRequestHeaders.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (method is not ("GET" or "POST" or "DELETE") || headers.Any(header => !header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && !header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, DELETE";
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            var authorization = context.Request.Headers.Authorization;
            var value = authorization.ToString();
            if (authorization.Count != 1 || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(value[7..]))))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
    }
}