namespace Carteiro.Domain;

/// <summary>
/// A subscriber: a URL that wants to receive events of certain types.
/// </summary>
/// <remarks>
/// Named WebhookEndpoint rather than Endpoint to stay out of the way of
/// <c>Microsoft.AspNetCore.Http.Endpoint</c>.
/// </remarks>
public sealed class WebhookEndpoint
{
    /// <summary>Subscribing to this value means "every event type".</summary>
    public const string AllEventTypes = "*";

    private WebhookEndpoint()
    {
    }

    public Guid Id { get; private set; }

    public string Url { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>Shared secret used to sign deliveries to this endpoint.</summary>
    public string Secret { get; private set; } = null!;

    public List<string> EventTypes { get; private set; } = [];

    public bool Enabled { get; private set; }

    /// <summary>
    /// Failed attempts since the last success, across all deliveries. The worker updates this
    /// with an atomic SQL statement, not through this object.
    /// </summary>
    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    public string? DisabledReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WebhookEndpoint Create(
        string url,
        string secret,
        IEnumerable<string> eventTypes,
        string? description,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return new WebhookEndpoint
        {
            Id = Guid.CreateVersion7(now),
            Url = url,
            Secret = secret,
            EventTypes = Normalize(eventTypes),
            Description = description,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsSubscribedTo(string eventType) =>
        EventTypes.Contains(AllEventTypes) || EventTypes.Contains(eventType);

    public void Update(string url, IEnumerable<string> eventTypes, string? description, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        Url = url;
        EventTypes = Normalize(eventTypes);
        Description = description;
        UpdatedAt = now;
    }

    /// <summary>
    /// Re-enables the endpoint and clears the failure streak, so an endpoint that was
    /// auto-disabled gets a full budget of failures again.
    /// </summary>
    public void Enable(DateTimeOffset now)
    {
        if (Enabled)
        {
            return;
        }

        Enabled = true;
        ConsecutiveFailures = 0;
        DisabledAt = null;
        DisabledReason = null;
        UpdatedAt = now;
    }

    public void Disable(string reason, DateTimeOffset now)
    {
        if (!Enabled)
        {
            return;
        }

        Enabled = false;
        DisabledAt = now;
        DisabledReason = reason;
        UpdatedAt = now;
    }

    private static List<string> Normalize(IEnumerable<string> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        var normalized = eventTypes.Select(t => t.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (normalized.Count == 0)
        {
            throw new ArgumentException("An endpoint must subscribe to at least one event type.", nameof(eventTypes));
        }

        return normalized;
    }
}
