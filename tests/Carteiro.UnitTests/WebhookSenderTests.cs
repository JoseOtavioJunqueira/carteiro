using System.Globalization;
using System.Net;
using Carteiro.Dispatch;
using Microsoft.Extensions.Options;

namespace Carteiro.UnitTests;

public class WebhookSenderTests
{
    private static readonly WebhookRequest Request = new(
        DeliveryId: Guid.NewGuid(),
        EventId: Guid.NewGuid(),
        EventType: "order.created",
        Payload: """{"orderId":42}""",
        Url: "https://receiver.example/hooks",
        Secret: "whsec_unit_test_secret",
        AttemptNumber: 3);

    [Fact]
    public async Task Sends_a_signed_post_with_delivery_headers()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var sender = CreateSender(new StubHandler(async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var outcome = await sender.SendAsync(Request, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(200, outcome.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(Request.Payload, body);
        Assert.Equal(Request.DeliveryId.ToString(), Header(captured, WebhookHeaders.DeliveryId));
        Assert.Equal("3", Header(captured, WebhookHeaders.Attempt));

        var timestamp = long.Parse(Header(captured, WebhookHeaders.Timestamp), CultureInfo.InvariantCulture);
        Assert.Equal(WebhookSigner.Sign(Request.Secret, timestamp, Request.Payload), Header(captured, WebhookHeaders.Signature));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Continue)]
    public async Task Anything_but_2xx_is_a_failure(HttpStatusCode statusCode)
    {
        var sender = CreateSender(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(statusCode))));

        var outcome = await sender.SendAsync(Request, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal((int)statusCode, outcome.StatusCode);
        Assert.Contains(((int)statusCode).ToString(CultureInfo.InvariantCulture), outcome.Error);
    }

    [Fact]
    public async Task A_slow_receiver_times_out()
    {
        var sender = CreateSender(
            new StubHandler(async request =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            timeout: TimeSpan.FromMilliseconds(200));

        var outcome = await sender.SendAsync(Request, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.StatusCode);
        Assert.Equal("No response within 0.2s.", outcome.Error);
    }

    [Fact]
    public async Task Connection_errors_become_failed_outcomes()
    {
        var sender = CreateSender(new StubHandler(_ => throw new HttpRequestException("boom", new IOException("connection reset"))));

        var outcome = await sender.SendAsync(Request, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("IOException: connection reset", outcome.Error);
    }

    [Fact]
    public async Task Shutdown_cancellation_is_not_swallowed()
    {
        using var shutdown = new CancellationTokenSource();
        var sender = CreateSender(new StubHandler(async request =>
        {
            await shutdown.CancelAsync();
            await Task.Delay(Timeout.Infinite, shutdown.Token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Request, shutdown.Token));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/hooks", "127.0.0.1")]
    [InlineData("http://localhost:9/hooks", "localhost")]
    [InlineData("http://[::1]:9/hooks", "::1")]
    public async Task The_real_handler_refuses_to_connect_to_private_addresses(string url, string expectedInError)
    {
        var options = new DeliveryOptions { AllowPrivateNetworks = false, RequestTimeout = TimeSpan.FromSeconds(5) };
        var sender = CreateSender(WebhookHttpHandler.Create(options));

        var outcome = await sender.SendAsync(Request with { Url = url }, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.StatusCode);
        Assert.StartsWith("Destination blocked", outcome.Error);
        Assert.Contains(expectedInError, outcome.Error);
    }

    [Fact]
    public async Task The_real_handler_connects_to_private_addresses_when_allowed()
    {
        // Nothing listens on the discard port, so the connection is refused, but that error
        // comes from the network stack, which means the guard let the attempt through.
        var options = new DeliveryOptions { AllowPrivateNetworks = true, RequestTimeout = TimeSpan.FromSeconds(5) };
        var sender = CreateSender(WebhookHttpHandler.Create(options));

        var outcome = await sender.SendAsync(Request with { Url = "http://127.0.0.1:9/hooks" }, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("Destination blocked", outcome.Error);
    }

    private static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();

    private static WebhookSender CreateSender(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var options = new CarteiroOptions();
        options.Delivery.RequestTimeout = timeout ?? TimeSpan.FromSeconds(5);
        return new WebhookSender(new SingleClientFactory(handler), Options.Create(options), TimeProvider.System);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request).WaitAsync(cancellationToken);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
