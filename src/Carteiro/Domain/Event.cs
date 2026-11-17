namespace Carteiro.Domain;

/// <summary>
/// Something that happened in a producer application and must be fanned out to subscribers.
/// </summary>
public sealed class Event
{
    private Event()
    {
    }

    public Guid Id { get; private set; }

    public string Type { get; private set; } = null!;

    /// <summary>
    /// The JSON document exactly as the producer sent it. It is stored as text, not jsonb,
    /// because the bytes that get signed and delivered must not be reformatted by the database.
    /// </summary>
    public string Payload { get; private set; } = null!;

    public string? IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Event Create(string type, string payload, string? idempotencyKey, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new Event
        {
            Id = Guid.CreateVersion7(now),
            Type = type,
            Payload = payload,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
        };
    }
}
