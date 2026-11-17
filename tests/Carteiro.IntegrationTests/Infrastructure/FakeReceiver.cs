using System.Collections.Concurrent;
using System.Net;

namespace Carteiro.IntegrationTests.Infrastructure;

/// <summary>A webhook request as the receiver saw it.</summary>
public sealed record ReceivedWebhook(string Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Header(string name) => Headers[name];
}

/// <summary>
/// Stands in for the receiving side of every webhook. Records what arrives and answers however
/// the current test tells it to.
/// </summary>
public sealed class FakeReceiver
{
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();
    private Func<ReceivedWebhook, Task<HttpStatusCode>> _respond = _ => Task.FromResult(HttpStatusCode.OK);

    public IReadOnlyList<ReceivedWebhook> Received => [.. _received];

    public void RespondWith(HttpStatusCode statusCode) => _respond = _ => Task.FromResult(statusCode);

    public void RespondWith(Func<ReceivedWebhook, Task<HttpStatusCode>> respond) => _respond = respond;

    public void Reset()
    {
        _received.Clear();
        RespondWith(HttpStatusCode.OK);
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        var webhook = new ReceivedWebhook(request.RequestUri!.ToString(), headers, body);
        _received.Enqueue(webhook);

        var statusCode = await _respond(webhook).WaitAsync(cancellationToken);
        return new HttpResponseMessage(statusCode);
    }
}

/// <summary>
/// Handler plugged into the webhook HttpClient. A new one is created whenever the client factory
/// rotates handlers, so it forwards to the shared <see cref="FakeReceiver"/> instead of owning state.
/// </summary>
internal sealed class FakeReceiverHandler(FakeReceiver receiver) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        receiver.HandleAsync(request, cancellationToken);
}
