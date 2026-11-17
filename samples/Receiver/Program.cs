// A minimal webhook receiver for the docker compose demo.
//
// It does what a real receiver should do: read the raw body, check the timestamp is recent,
// recompute the HMAC and compare it in constant time. It deliberately does not reference the
// Carteiro project, to show that verification needs nothing but the shared secret.
//
//   POST /hooks               verifies the signature, then answers with the current mode's status
//   POST /hooks/always-fail   verifies the signature, then always answers 503
//   PUT  /mode/{statusCode}   changes what /hooks answers (e.g. 500 to simulate an outage, 200 to recover)
//   GET  /received            the last 100 requests and whether their signature was valid

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var secret = app.Configuration["RECEIVER_SECRET"]
    ?? throw new InvalidOperationException("Set RECEIVER_SECRET to the secret the endpoint was registered with.");
var tolerance = TimeSpan.FromMinutes(5);

var received = new ConcurrentQueue<ReceivedWebhook>();
var mode = StatusCodes.Status200OK;

app.MapPost("/hooks", (HttpRequest request) => Handle(request, () => mode));
app.MapPost("/hooks/always-fail", (HttpRequest request) => Handle(request, () => StatusCodes.Status503ServiceUnavailable));

app.MapPut("/mode/{statusCode:int}", (int statusCode) =>
{
    mode = statusCode;
    app.Logger.LogInformation("/hooks will now answer {StatusCode}", statusCode);
    return Results.Ok(new { mode });
});

app.MapGet("/received", () => received.Reverse());

app.Run();

async Task<IResult> Handle(HttpRequest request, Func<int> statusCode)
{
    // Verify against the exact bytes received. Parsing and re-serializing the JSON first would
    // change whitespace or key order and break the signature.
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();

    var deliveryId = request.Headers["Carteiro-Delivery-Id"].ToString();
    var attempt = request.Headers["Carteiro-Attempt"].ToString();
    var valid = Verify(
        request.Headers["Carteiro-Timestamp"].ToString(),
        request.Headers["Carteiro-Signature"].ToString(),
        body);

    received.Enqueue(new ReceivedWebhook(DateTimeOffset.UtcNow, deliveryId, attempt, valid, body));
    while (received.Count > 100)
    {
        received.TryDequeue(out _);
    }

    if (!valid)
    {
        app.Logger.LogWarning("Rejected delivery {DeliveryId}: invalid signature", deliveryId);
        return Results.Unauthorized();
    }

    // A real receiver would now check deliveryId against the ones it already processed:
    // delivery is at-least-once, so the same delivery can arrive more than once.
    var status = statusCode();
    app.Logger.LogInformation("Delivery {DeliveryId} attempt {Attempt}: signature valid, answering {Status}", deliveryId, attempt, status);
    return Results.StatusCode(status);
}

bool Verify(string timestampHeader, string signatureHeader, string body)
{
    if (!long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
    {
        return false;
    }

    // Reject old (replayed) or far-future timestamps.
    var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp);
    if (age.Duration() > tolerance)
    {
        return false;
    }

    const string prefix = "v1=";
    if (!signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
    {
        return false;
    }

    var expected = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret),
        Encoding.UTF8.GetBytes($"{timestamp}.{body}"));

    byte[] provided;
    try
    {
        provided = Convert.FromHexString(signatureHeader[prefix.Length..]);
    }
    catch (FormatException)
    {
        return false;
    }

    return CryptographicOperations.FixedTimeEquals(expected, provided);
}

internal sealed record ReceivedWebhook(DateTimeOffset At, string DeliveryId, string Attempt, bool SignatureValid, string Body);
