namespace Carteiro.Domain;

/// <summary>
/// One HTTP request made for a delivery. Append-only: attempts are never updated.
/// </summary>
public sealed class DeliveryAttempt
{
    private DeliveryAttempt()
    {
    }

    public Guid Id { get; private set; }

    public Guid DeliveryId { get; private set; }

    /// <summary>1-based position of this attempt within its delivery.</summary>
    public int AttemptNumber { get; private set; }

    public bool Succeeded { get; private set; }

    /// <summary>HTTP status returned by the receiver, or null if no response was received.</summary>
    public int? StatusCode { get; private set; }

    public int DurationMs { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    internal static DeliveryAttempt Create(Guid deliveryId, int attemptNumber, AttemptOutcome outcome, DateTimeOffset attemptedAt)
    {
        return new DeliveryAttempt
        {
            Id = Guid.CreateVersion7(attemptedAt),
            DeliveryId = deliveryId,
            AttemptNumber = attemptNumber,
            Succeeded = outcome.Succeeded,
            StatusCode = outcome.StatusCode,
            DurationMs = (int)Math.Min(int.MaxValue, Math.Round(outcome.Duration.TotalMilliseconds)),
            Error = outcome.Error,
            AttemptedAt = attemptedAt,
        };
    }
}
