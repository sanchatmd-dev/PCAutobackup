using System.Net;
using System.Net.Sockets;
using System.Text;
using PCAutobackup.Core.Google;

namespace PCAutobackup.Tests;

// OAuth loopback callback validation. Loopback sockets only; no browser and no external network.
public sealed class AuditOAuthTests : IDisposable
{
    private const string State = "expected-state";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly int port;
    public AuditOAuthTests() { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
    public void Dispose() => listener.Stop();

    private Task<string> Read(CancellationToken ct) =>
        GoogleAccountService.ReadCallbackAsync(listener, port, State, "en", ct, TimeSpan.FromMilliseconds(300));

    private async Task<string> SendAsync(string requestLine)
    {
        // Bounded: if the listener stopped accepting, fail the test instead of waiting for a reply forever.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestLine + "\r\nHost: 127.0.0.1\r\n\r\n"), limit.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(limit.Token);
    }

    [Fact]
    public async Task ValidCallbackReturnsCodeAndFixedPage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var callback = Read(timeout.Token);
        var page = await SendAsync($"GET /oauth2callback?state={State}&code=abc%2F123 HTTP/1.1");
        Assert.Equal("abc/123", await callback);
        Assert.StartsWith("HTTP/1.1 200 OK", page);
        Assert.DoesNotContain("abc", page); // nothing from the request is reflected
    }

    [Fact]
    public async Task IdleAndDroppedConnectionsDoNotAbortSignIn()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var callback = Read(timeout.Token);
        // A browser preconnect: connected, sends nothing, stays open past the per-connection timeout.
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);
        // A probe that connects and resets without a request.
        using (var dropped = new TcpClient())
        {
            await dropped.ConnectAsync(IPAddress.Loopback, port);
            dropped.LingerState = new LingerOption(true, 0);
        }
        // An unrelated request on the same port is answered and ignored.
        Assert.StartsWith("HTTP/1.1 400", await SendAsync("GET /favicon.ico HTTP/1.1"));
        var page = await SendAsync($"GET /oauth2callback?code=the-code&state={State} HTTP/1.1");
        Assert.Equal("the-code", await callback);
        Assert.StartsWith("HTTP/1.1 200 OK", page);
    }

    [Theory]
    [InlineData("GET /oauth2callback?state=wrong&code=abc HTTP/1.1")]
    [InlineData("GET /oauth2callback?code=abc HTTP/1.1")]
    [InlineData("GET /oauth2callback?state=expected-state&error=access_denied HTTP/1.1")]
    [InlineData("GET /oauth2callback?state=expected-state HTTP/1.1")]
    [InlineData("GET /oauth2callback?state=expected-state&state=expected-state&code=abc HTTP/1.1")]
    public async Task WrongStateDeniedConsentMissingCodeAndDuplicateParametersAreRejected(string requestLine)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var callback = Read(timeout.Token);
        var page = await SendAsync(requestLine);
        await Assert.ThrowsAsync<InvalidOperationException>(() => callback);
        Assert.DoesNotContain("200 OK", page);
    }

    [Fact]
    public async Task CallerCancellationStillEndsTheWait()
    {
        using var cancel = new CancellationTokenSource();
        var callback = Read(cancel.Token);
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback);
    }
}
