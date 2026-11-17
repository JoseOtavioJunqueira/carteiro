using Carteiro.Data;
using Carteiro.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Carteiro.Dispatch;

/// <summary>
/// A delivery this processor holds the lease for, with everything needed to send it.
/// </summary>
internal sealed record ClaimedDelivery(Guid DeliveryId, Guid EndpointId, Guid LockToken, WebhookRequest Request);

/// <summary>
/// Claims due deliveries, sends them, and records the results.
/// </summary>
/// <remarks>
/// Each delivery goes through three steps, and only the first and last touch the database:
/// <list type="number">
/// <item>Claim: a short transaction leases a batch of due rows using FOR UPDATE SKIP LOCKED.</item>
/// <item>Send: the HTTP request is made with no transaction or connection held.</item>
/// <item>Complete: a short transaction records the attempt and moves the state machine.</item>
/// </list>
/// A crash between 1 and 3 leaves the row leased; when the lease expires another worker picks it
/// up and sends again. That is where at-least-once comes from.
/// </remarks>
internal sealed partial class DeliveryProcessor(
    IDbContextFactory<CarteiroDbContext> dbContextFactory,
    WebhookSender sender,
    DeliveryMetrics metrics,
    IOptions<CarteiroOptions> options,
    TimeProvider time,
    ILogger<DeliveryProcessor> logger)
{
    private readonly DeliveryOptions _delivery = options.Value.Delivery;
    private readonly WorkerOptions _worker = options.Value.Worker;
    private readonly RetryPolicy _retryPolicy = options.Value.Delivery.ToRetryPolicy();

    /// <summary>
    /// Runs one round: claim a batch, send everything in it concurrently, record the results.
    /// </summary>
    /// <returns>The number of deliveries claimed in this round.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        if (claimed.Count == 0)
        {
            return 0;
        }

        var parallelism = new ParallelOptions
        {
            MaxDegreeOfParallelism = claimed.Count,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(claimed, parallelism, async (delivery, ct) => await ProcessAsync(delivery, ct));

        return claimed.Count;
    }

    /// <summary>
    /// Leases up to BatchSize due deliveries for this processor.
    /// </summary>
    internal async Task<IReadOnlyList<ClaimedDelivery>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var now = time.GetUtcNow();
        var lockedUntil = now + _worker.ClaimLease;
        var lockToken = Guid.NewGuid();
        var batchSize = _worker.BatchSize;

        // The heart of the queue.
        //
        // FOR UPDATE locks the selected rows until this statement commits, and SKIP LOCKED makes a
        // concurrent worker step over rows that are locked instead of waiting for them. Two
        // workers running this at the same instant therefore get disjoint batches.
        //
        // Once the statement commits, the row locks are gone and locked_until takes over: it
        // hides the rows from other workers for as long as the HTTP request may take. If another
        // worker had already read an old version of a row, PostgreSQL re-checks the WHERE clause
        // against the committed version before locking it (READ COMMITTED), sees the fresh
        // locked_until, and skips it.
        //
        // "OF d" keeps the lock on the delivery only; endpoint rows are read, never locked.
        // Deliveries of disabled endpoints are left alone: they resume when the endpoint is
        // enabled again.
        var deliveries = await db.Deliveries
            .FromSql($"""
                WITH due AS (
                    SELECT d.id
                    FROM deliveries d
                    JOIN endpoints e ON e.id = d.endpoint_id
                    WHERE d.status IN ('Pending', 'Failed')
                      AND d.next_attempt_at <= {now}
                      AND (d.locked_until IS NULL OR d.locked_until <= {now})
                      AND e.enabled
                    ORDER BY d.next_attempt_at
                    LIMIT {batchSize}
                    FOR UPDATE OF d SKIP LOCKED
                )
                UPDATE deliveries
                SET locked_until = {lockedUntil}, lock_token = {lockToken}
                FROM due
                WHERE deliveries.id = due.id
                RETURNING deliveries.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (deliveries.Count == 0)
        {
            return [];
        }

        var eventIds = deliveries.Select(d => d.EventId).Distinct().ToList();
        var endpointIds = deliveries.Select(d => d.EndpointId).Distinct().ToList();

        var events = await db.Events.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var endpoints = await db.Endpoints.AsNoTracking()
            .Where(e => endpointIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var claimed = new List<ClaimedDelivery>(deliveries.Count);
        foreach (var delivery in deliveries)
        {
            // An endpoint deleted a moment ago takes its deliveries with it; nothing to send.
            if (!events.TryGetValue(delivery.EventId, out var @event) ||
                !endpoints.TryGetValue(delivery.EndpointId, out var endpoint))
            {
                continue;
            }

            claimed.Add(new ClaimedDelivery(
                delivery.Id,
                delivery.EndpointId,
                lockToken,
                new WebhookRequest(
                    delivery.Id,
                    @event.Id,
                    @event.Type,
                    @event.Payload,
                    endpoint.Url,
                    endpoint.Secret,
                    AttemptNumber: delivery.AttemptCount + 1)));
        }

        return claimed;
    }

    private async Task ProcessAsync(ClaimedDelivery claimed, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await sender.SendAsync(claimed.Request, cancellationToken);

            // Not cancellable on purpose: the request has been made, so even during shutdown we
            // want its result on record rather than a duplicate send after the lease expires.
            await CompleteAsync(claimed, outcome, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-request. The lease expires and another worker retries.
            throw;
        }
        catch (Exception ex)
        {
            // Typically the database being unreachable when recording the result. The lease
            // expires and the delivery is attempted again; one bad delivery must not take the
            // rest of the batch down with it.
            Log.ProcessingFailed(logger, ex, claimed.DeliveryId);
        }
    }

    /// <summary>
    /// Records the result of an attempt, provided this processor still owns the lease.
    /// </summary>
    /// <returns>False if the lease was lost and the result was discarded.</returns>
    internal async Task<bool> CompleteAsync(ClaimedDelivery claimed, AttemptOutcome outcome, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var delivery = (await db.Deliveries
            .FromSql($"SELECT * FROM deliveries WHERE id = {claimed.DeliveryId} FOR UPDATE")
            .ToListAsync(cancellationToken))
            .SingleOrDefault();

        // Fencing. If our lease expired (a long GC pause, a stalled connection) another worker
        // may have claimed the row under a new token and may even have finished it. Writing our
        // result now would corrupt the attempt count and could resurrect a completed delivery.
        if (delivery is null || delivery.LockToken != claimed.LockToken)
        {
            Log.LeaseLost(logger, claimed.DeliveryId);
            return false;
        }

        var now = time.GetUtcNow();
        var attempt = delivery.RecordAttempt(outcome, _retryPolicy, Random.Shared.NextDouble(), now);

        var endpointDisabled = outcome.Succeeded
            ? await ResetFailureStreakAsync(db, claimed.EndpointId, cancellationToken)
            : await RegisterFailureAsync(db, claimed.EndpointId, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        metrics.AttemptRecorded(outcome, delivery.Status);

        if (outcome.Succeeded)
        {
            Log.DeliverySucceeded(logger, delivery.Id, attempt.AttemptNumber, attempt.StatusCode, attempt.DurationMs);
        }
        else if (delivery.Status == DeliveryStatus.Dead)
        {
            Log.DeliveryDead(logger, delivery.Id, attempt.AttemptNumber, attempt.Error);
        }
        else
        {
            Log.DeliveryFailed(logger, delivery.Id, attempt.AttemptNumber, attempt.Error, delivery.NextAttemptAt);
        }

        if (endpointDisabled)
        {
            metrics.EndpointDisabled();
            Log.EndpointDisabled(logger, claimed.EndpointId, _delivery.EndpointFailureThreshold);
        }

        return true;
    }

    private static async Task<bool> ResetFailureStreakAsync(CarteiroDbContext db, Guid endpointId, CancellationToken cancellationToken)
    {
        // The extra predicate makes the common case (streak already zero) a no-op that does not
        // write, or lock, the endpoint row.
        await db.Endpoints
            .Where(e => e.Id == endpointId && e.ConsecutiveFailures != 0)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ConsecutiveFailures, 0), cancellationToken);

        return false;
    }

    /// <summary>
    /// Counts a failure against the endpoint and disables it if the streak reached the threshold.
    /// </summary>
    /// <remarks>
    /// The increment is done in SQL (<c>SET consecutive_failures = consecutive_failures + 1</c>),
    /// not read-modify-write in memory, so concurrent workers failing against the same endpoint
    /// cannot lose each other's updates.
    /// </remarks>
    private async Task<bool> RegisterFailureAsync(CarteiroDbContext db, Guid endpointId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.Endpoints
            .Where(e => e.Id == endpointId)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ConsecutiveFailures, x => x.ConsecutiveFailures + 1), cancellationToken);

        var threshold = _delivery.EndpointFailureThreshold;
        if (threshold <= 0)
        {
            return false;
        }

        var reason = $"Disabled automatically after {threshold} consecutive failed attempts.";

        var disabled = await db.Endpoints
            .Where(e => e.Id == endpointId && e.Enabled && e.ConsecutiveFailures >= threshold)
            .ExecuteUpdateAsync(
                e => e
                    .SetProperty(x => x.Enabled, false)
                    .SetProperty(x => x.DisabledAt, now)
                    .SetProperty(x => x.DisabledReason, reason)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        return disabled > 0;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Delivery {DeliveryId} succeeded on attempt {Attempt} with HTTP {StatusCode} in {DurationMs} ms")]
        public static partial void DeliverySucceeded(ILogger logger, Guid deliveryId, int attempt, int? statusCode, int durationMs);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery {DeliveryId} failed on attempt {Attempt}: {Error}. Next attempt at {NextAttemptAt}")]
        public static partial void DeliveryFailed(ILogger logger, Guid deliveryId, int attempt, string? error, DateTimeOffset? nextAttemptAt);

        [LoggerMessage(Level = LogLevel.Error, Message = "Delivery {DeliveryId} is dead after {Attempt} attempts: {Error}")]
        public static partial void DeliveryDead(ILogger logger, Guid deliveryId, int attempt, string? error);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint {EndpointId} disabled after {Threshold} consecutive failures")]
        public static partial void EndpointDisabled(ILogger logger, Guid endpointId, int threshold);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Lease on delivery {DeliveryId} was lost before its result could be recorded; discarding the result")]
        public static partial void LeaseLost(ILogger logger, Guid deliveryId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while processing delivery {DeliveryId}")]
        public static partial void ProcessingFailed(ILogger logger, Exception exception, Guid deliveryId);
    }
}
