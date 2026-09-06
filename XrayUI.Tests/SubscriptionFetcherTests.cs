using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using XrayUI.Models;
using XrayUI.Services;

namespace XrayUI.Tests;

public class SubscriptionFetcherTests
{
    private const string Nodes = "vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls&type=tcp#Test";

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.True(Requests < responses.Length, "Unexpected additional subscription request");
            return Task.FromResult(responses[Requests++]);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task MetadataRateLimitKeepsValidNodes_AndPreservesCooldownAfterSuccess()
    {
        var until = DateTimeOffset.UtcNow.AddHours(2);
        var limited = Response(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(until);
        var handler = new SequenceHandler(Response(HttpStatusCode.OK, Nodes), limited);
        using var client = new HttpClient(handler);
        var sub = new SubscriptionEntry { AutoRefreshIntervalMinutes = 60, Url = "https://example.com/sub" };
        var result = await SubscriptionFetcher.FetchNodesAsync(sub, client, direct: true);
        Assert.Single(result.entries!);
        Assert.Null(result.error);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(until, sub.RetryAfterUtc);
        SubscriptionRefreshSchedule.RecordSuccess(sub, DateTimeOffset.UtcNow);
        Assert.Equal(0, sub.RefreshFailureCount);
        Assert.False(SubscriptionRefreshSchedule.IsDue(sub, until.AddMinutes(-1), true, true));
        Assert.True(SubscriptionRefreshSchedule.IsDue(sub, until));
    }

    [Fact]
    public async Task PrimaryRateLimitStopsFurtherRequests_AndRecordsOneFailure()
    {
        var limited = Response(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
        var handler = new SequenceHandler(limited);
        using var client = new HttpClient(handler);
        var sub = new SubscriptionEntry { AutoRefreshIntervalMinutes = 60, Url = "https://example.com/sub" };
        var before = DateTimeOffset.UtcNow;
        var result = await SubscriptionFetcher.FetchNodesAsync(sub, client, direct: false);
        Assert.Null(result.entries);
        Assert.Contains("429", result.error!);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, sub.RefreshFailureCount);
        Assert.True(sub.RetryAfterUtc >= before.AddMinutes(20));
        Assert.False(sub.LastFailureWasDirect);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task PermanentFailureDoesNotRequestMetadata_OrDiscardLastSuccess(int status)
    {
        var handler = new SequenceHandler(Response((HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var updated = DateTimeOffset.UtcNow.AddDays(-1);
        var sub = new SubscriptionEntry { AutoRefreshIntervalMinutes = 360,
            Url = "https://example.com/sub", LastUpdated = updated };
        var result = await SubscriptionFetcher.FetchNodesAsync(sub, client, direct: true);
        Assert.Null(result.entries);
        Assert.Contains(status.ToString(), result.error!);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(updated, sub.LastUpdated);
        Assert.True(sub.LastFailurePermanent);
        Assert.Equal(1, sub.RefreshFailureCount);
    }

    [Fact]
    public async Task MalformedExpiryHeaderCannotRejectValidNodes()
    {
        var response = Response(HttpStatusCode.OK, Nodes);
        response.Headers.TryAddWithoutValidation("subscription-userinfo", "total=1024; expire=9223372036854775807");
        var handler = new SequenceHandler(response);
        using var client = new HttpClient(handler);
        var sub = new SubscriptionEntry { Url = "https://example.com/sub" };
        var result = await SubscriptionFetcher.FetchNodesAsync(sub, client, direct: true);
        Assert.Single(result.entries!);
        Assert.Null(result.error);
        Assert.Null(sub.Usage.Expire);
        Assert.Equal(1024L, sub.Usage.Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not a subscription")]
    public async Task InvalidBodiesReturnFailureInsteadOfAnEmptyReplacement(string body)
    {
        var handler = new SequenceHandler(Response(HttpStatusCode.OK, body), Response(HttpStatusCode.OK, body));
        using var client = new HttpClient(handler);
        var sub = new SubscriptionEntry { AutoRefreshIntervalMinutes = 360, Url = "https://example.com/sub" };
        var result = await SubscriptionFetcher.FetchNodesAsync(sub, client, direct: true);
        Assert.Null(result.entries);
        Assert.NotNull(result.error);
        Assert.Equal(1, sub.RefreshFailureCount);
        Assert.NotNull(sub.NextRetryAt);
    }

    [Fact]
    public async Task DirectClientCanFetchFromLocalHttpServer()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new System.IO.StreamReader(stream, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            var response = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response, timeout.Token);
        }, TestContext.Current.CancellationToken);
        using var client = SubscriptionFetcher.CreateClient(null);
        Assert.Equal("OK", await client.GetStringAsync($"http://127.0.0.1:{port}/sub", timeout.Token));
        await serve;
    }

    [Fact]
    public async Task RejectedSocksConnectionDoesNotFallBackToOrigin()
    {
        using var origin = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        using var proxy = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        proxy.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reject = Task.Run(async () =>
        {
            using var connection = await proxy.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(5, greeting[0]);
            await stream.WriteAsync(new byte[] { 5, 255 }, timeout.Token);
        }, TestContext.Current.CancellationToken);
        using var client = SubscriptionFetcher.CreateClient(proxyPort);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetStringAsync($"http://127.0.0.1:{originPort}/sub", timeout.Token));
        await reject;
        Assert.False(origin.Pending(), "A failed SOCKS request must not contact the origin directly.");
    }
}
