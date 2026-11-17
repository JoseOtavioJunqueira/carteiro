using System.Globalization;
using System.Text;
using Carteiro.Domain;
using Microsoft.Extensions.Options;

namespace Carteiro.Dispatch;

/// <summary>Headers sent with every webhook request.</summary>
public static class WebhookHeaders
{
    /// <summary>Delivery id. Stable across retries, so receivers can use it to deduplicate.</summary>
    public const string DeliveryId = "Carteiro-Delivery-Id";

    public const string EventId = "Carteiro-Event-Id";

    public const string EventType = "Carteiro-Event-Type";

    /// <summary>1-based attempt number.</summary>
    public const string Attempt = "Carteiro-Attempt";

    /// <summary>Unix time in seconds at which the request was signed.</summary>
    public const string Timestamp = "Carteiro-Timestamp";

    /// <summary><c>v1=</c> followed by the hex HMAC-SHA256 of <c>{timestamp}.{body}</c>.</summary>
    public const string Signature = "Carteiro-Signature";
}

/// <summary>Everything needed to make one webhook request.</summary>
public sealed record WebhookRequest(
    Guid DeliveryId,
    Guid EventId,
    string EventType,
    string Payload,
    string Url,
    string Secret,
    int AttemptNumber);

/// <summary>
/// Makes the HTTP request for a delivery and reports what happened. It never throws for a failed
/// delivery: timeouts, connection errors and non-2xx answers all come back as a failed outcome.
/// </summary>
internal sealed class WebhookSender(
    IHttpClientFactory httpClientFactory,
    IOptions<CarteiroOptions> options,
    TimeProvider time)
{
    public const string HttpClientName = "carteiro-webhooks";

    private const int MaxErrorLength = 500;

    private readonly TimeSpan _requestTimeout = options.Value.Delivery.RequestTimeout;

    public async Task<AttemptOutcome> SendAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();

        // The timeout covers the whole exchange up to the response headers, connect included.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        try
        {
            using var message = BuildMessage(request);
            var client = httpClientFactory.CreateClient(HttpClientName);

            // We only care about the status line. Not buffering the body also means a receiver
            // cannot make us hold a large response in memory.
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var statusCode = (int)response.StatusCode;
            var elapsed = time.GetElapsedTime(started);

            return response.IsSuccessStatusCode
                ? AttemptOutcome.Success(statusCode, elapsed)
                : AttemptOutcome.Failure(statusCode, elapsed, $"Receiver responded with HTTP {statusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AttemptOutcome.Failure(
                null,
                time.GetElapsedTime(started),
                $"No response within {_requestTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or InvalidOperationException)
        {
            return AttemptOutcome.Failure(null, time.GetElapsedTime(started), Describe(ex));
        }
    }

    private HttpRequestMessage BuildMessage(WebhookRequest request)
    {
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();

        var message = new HttpRequestMessage(HttpMethod.Post, request.Url)
        {
            Content = new StringContent(request.Payload, Encoding.UTF8, "application/json"),
        };

        message.Headers.TryAddWithoutValidation("User-Agent", "Carteiro/1.0");
        message.Headers.Add(WebhookHeaders.DeliveryId, request.DeliveryId.ToString());
        message.Headers.Add(WebhookHeaders.EventId, request.EventId.ToString());
        message.Headers.Add(WebhookHeaders.EventType, request.EventType);
        message.Headers.Add(WebhookHeaders.Attempt, request.AttemptNumber.ToString(CultureInfo.InvariantCulture));
        message.Headers.Add(WebhookHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.Add(WebhookHeaders.Signature, WebhookSigner.Sign(request.Secret, timestamp, request.Payload));

        return message;
    }

    private static string Describe(Exception exception)
    {
        // HttpClient wraps the interesting error (connection refused, blocked destination, TLS
        // failure) one or two levels down.
        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        var message = root is BlockedDestinationException
            ? root.Message
            : $"{root.GetType().Name}: {root.Message}";

        return message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
    }
}
