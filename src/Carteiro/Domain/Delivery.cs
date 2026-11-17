using Carteiro.Dispatch;

namespace Carteiro.Domain;

/// <summary>
/// The obligation to deliver one event to one endpoint, and the state machine around it.
/// </summary>
/// <remarks>
/// Transitions:
/// <list type="bullet">
/// <item>Pending or Failed, attempt succeeds: Succeeded (terminal).</item>
/// <item>Pending or Failed, attempt fails with attempts left: Failed, with a new NextAttemptAt.</item>
/// <item>Pending or Failed, attempt fails with no attempts left: Dead.</item>
/// <item>Dead, manual retry: Pending.</item>
/// </list>
/// </remarks>
public sealed class Delivery
{
    private readonly List<DeliveryAttempt> _attempts = [];

    private Delivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid EndpointId { get; private set; }

    public DeliveryStatus Status { get; private set; }

    /// <summary>Total attempts ever made, manual retries included.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>When the worker may pick this delivery up. Null once it is Succeeded or Dead.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>
    /// Lease expiry. While this is in the future a worker is (presumably) mid-request and other
    /// workers must leave the row alone. If the worker dies, the lease expires and the delivery
    /// becomes claimable again.
    /// </summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>
    /// Identifies the claim that currently owns the lease. A worker may only record a result
    /// if its token still matches, which fences off workers whose lease expired mid-flight.
    /// </summary>
    public Guid? LockToken { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Event? Event { get; private set; }

    public WebhookEndpoint? Endpoint { get; private set; }

    public IReadOnlyList<DeliveryAttempt> Attempts => _attempts;

    public bool IsTerminal => Status is DeliveryStatus.Succeeded or DeliveryStatus.Dead;

    public static Delivery Create(Guid eventId, Guid endpointId, DateTimeOffset now)
    {
        return new Delivery
        {
            Id = Guid.CreateVersion7(now),
            EventId = eventId,
            EndpointId = endpointId,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Applies the result of an attempt and returns the attempt record to persist.
    /// </summary>
    /// <param name="outcome">What happened on the wire.</param>
    /// <param name="policy">Retry policy consulted when the attempt failed.</param>
    /// <param name="jitterSample">Uniform random sample in [0, 1) forwarded to the policy.</param>
    /// <param name="now">Current time.</param>
    public DeliveryAttempt RecordAttempt(AttemptOutcome outcome, RetryPolicy policy, double jitterSample, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(policy);

        if (IsTerminal)
        {
            throw new InvalidOperationException($"Delivery {Id} is {Status}; no further attempts can be recorded.");
        }

        AttemptCount++;
        LockedUntil = null;
        LockToken = null;

        if (outcome.Succeeded)
        {
            Status = DeliveryStatus.Succeeded;
            NextAttemptAt = null;
            CompletedAt = now;
            LastError = null;
        }
        else
        {
            LastError = outcome.Error;

            var decision = policy.Next(AttemptCount, jitterSample);
            if (decision.ShouldRetry)
            {
                Status = DeliveryStatus.Failed;
                NextAttemptAt = now + decision.Delay;
            }
            else
            {
                Status = DeliveryStatus.Dead;
                NextAttemptAt = null;
                CompletedAt = now;
            }
        }

        var attempt = DeliveryAttempt.Create(Id, AttemptCount, outcome, now);
        _attempts.Add(attempt);
        return attempt;
    }

    /// <summary>
    /// Puts a dead delivery back in the queue for one more attempt. The attempt count is kept,
    /// so if that attempt fails the delivery goes straight back to Dead instead of starting
    /// a whole new backoff cycle.
    /// </summary>
    public void RetryManually(DateTimeOffset now)
    {
        if (Status != DeliveryStatus.Dead)
        {
            throw new InvalidOperationException($"Only dead deliveries can be retried manually; delivery {Id} is {Status}.");
        }

        Status = DeliveryStatus.Pending;
        NextAttemptAt = now;
        CompletedAt = null;
        LockedUntil = null;
        LockToken = null;
    }
}
