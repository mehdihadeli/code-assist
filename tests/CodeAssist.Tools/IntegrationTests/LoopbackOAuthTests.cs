using System.Net;
using System.Net.Sockets;
using CodeAssist.Mcp;
using ModelContextProtocol.Authentication;
using Shouldly;

namespace CodeAssist.Tools.IntegrationTests;

public sealed class LoopbackOAuthTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_ReturnOnlyUnambiguousCallbackParameters_When_BrowserCompletesAuthorization(bool valid)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var http = new HttpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var redirect = new Uri($"http://127.0.0.1:{port}/callback");
        Task<HttpResponseMessage>? response = null;
        var callback = new LoopbackOAuthCallback((_, token) =>
        {
            response = http.GetAsync(new Uri(redirect + "?code=fixture-code&state=bound-state&iss=https%3A%2F%2Fissuer.example" + (valid ? "" : "&state=duplicate")), token);
            return Task.CompletedTask;
        });
        var result = await callback.AuthorizeAsync(new AuthorizationCallbackContext { AuthorizationUri = new Uri("https://issuer.example/authorize"), RedirectUri = redirect }, timeout.Token);
        using var completed = await response.ShouldNotBeNull();
        if (valid)
        {
            result.ShouldNotBeNull().State.ShouldBe("bound-state");
            result.Iss.ShouldBe("https://issuer.example");
            completed.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await completed.Content.ReadAsStringAsync(timeout.Token)).ShouldNotContain("fixture-code");
        }
        else
        {
            result.ShouldBeNull();
            completed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }
}