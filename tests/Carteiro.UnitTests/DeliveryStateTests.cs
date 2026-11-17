using Carteiro.Dispatch;
using Carteiro.Domain;

namespace Carteiro.UnitTests;

public class DeliveryStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly RetryPolicy Policy = new(maxAttempts: 3, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));

    private static readonly AttemptOutcome Ok = AttemptOutcome.Success(200, TimeSpan.FromMilliseconds(42));
    private static readonly AttemptOutcome ServerError = AttemptOutcome.Failure(500, TimeSpan.FromMilliseconds(17), "HTTP 500");

    private static Delivery NewDelivery() => Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), T0);

    [Fact]
    public void A_new_delivery_is_pending_and_due_immediately()
    {
        var delivery = NewDelivery();

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Equal(T0, delivery.NextAttemptAt);
        Assert.Null(delivery.CompletedAt);
        Assert.False(delivery.IsTerminal);
    }

    [Fact]
    public void A_successful_attempt_completes_the_delivery()
    {
        var delivery = NewDelivery();
        var now = T0.AddSeconds(1);

        var attempt = delivery.RecordAttempt(Ok, Policy, 0.5, now);

        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Equal(now, delivery.CompletedAt);
        Assert.True(delivery.IsTerminal);

        Assert.Equal(delivery.Id, attempt.DeliveryId);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.True(attempt.Succeeded);
        Assert.Equal(200, attempt.StatusCode);
        Assert.Equal(42, attempt.DurationMs);
        Assert.Null(attempt.Error);
        Assert.Equal(now, attempt.AttemptedAt);
    }

    [Fact]
    public void A_failed_attempt_schedules_a_retry_using_the_policy()
    {
        var delivery = NewDelivery();
        var now = T0.AddSeconds(1);

        var attempt = delivery.RecordAttempt(ServerError, Policy, jitterSample: 0.0, now);

        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(now + Policy.Next(1, 0.0).Delay, delivery.NextAttemptAt);
        Assert.Equal("HTTP 500", delivery.LastError);
        Assert.Null(delivery.CompletedAt);
        Assert.False(delivery.IsTerminal);

        Assert.False(attempt.Succeeded);
        Assert.Equal(500, attempt.StatusCode);
        Assert.Equal("HTTP 500", attempt.Error);
    }

    [Fact]
    public void Backoff_grows_between_consecutive_failures()
    {
        var delivery = NewDelivery();

        delivery.RecordAttempt(ServerError, Policy, 0.0, T0);
        var firstWait = delivery.NextAttemptAt!.Value - T0;

        delivery.RecordAttempt(ServerError, Policy, 0.0, T0);
        var secondWait = delivery.NextAttemptAt!.Value - T0;

        Assert.Equal(firstWait * 2, secondWait);
    }

    [Fact]
    public void The_delivery_is_dead_lettered_when_attempts_run_out()
    {
        var delivery = NewDelivery();
        var now = T0;

        for (var i = 0; i < Policy.MaxAttempts; i++)
        {
            now = now.AddMinutes(1);
            delivery.RecordAttempt(ServerError, Policy, 0.5, now);
        }

        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        Assert.Equal(Policy.MaxAttempts, delivery.AttemptCount);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Equal(now, delivery.CompletedAt);
        Assert.True(delivery.IsTerminal);
        Assert.Equal([1, 2, 3], delivery.Attempts.Select(a => a.AttemptNumber));
    }

    [Fact]
    public void A_failed_delivery_can_still_succeed_on_a_later_attempt()
    {
        var delivery = NewDelivery();

        delivery.RecordAttempt(ServerError, Policy, 0.5, T0);
        delivery.RecordAttempt(Ok, Policy, 0.5, T0.AddMinutes(1));

        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Null(delivery.LastError);
    }

    [Fact]
    public void An_attempt_without_a_response_has_no_status_code()
    {
        var delivery = NewDelivery();

        var attempt = delivery.RecordAttempt(
            AttemptOutcome.Failure(null, TimeSpan.FromSeconds(10), "Timed out after 10s"), Policy, 0.5, T0);

        Assert.Null(attempt.StatusCode);
        Assert.Equal(10_000, attempt.DurationMs);
        Assert.Equal("Timed out after 10s", delivery.LastError);
    }

    [Fact]
    public void No_attempt_can_be_recorded_on_a_succeeded_delivery()
    {
        var delivery = NewDelivery();
        delivery.RecordAttempt(Ok, Policy, 0.5, T0);

        Assert.Throws<InvalidOperationException>(() => delivery.RecordAttempt(Ok, Policy, 0.5, T0));
        Assert.Equal(1, delivery.AttemptCount);
    }

    [Fact]
    public void No_attempt_can_be_recorded_on_a_dead_delivery()
    {
        var delivery = DeadDelivery();

        Assert.Throws<InvalidOperationException>(() => delivery.RecordAttempt(Ok, Policy, 0.5, T0));
    }

    [Fact]
    public void A_manual_retry_requeues_a_dead_delivery()
    {
        var delivery = DeadDelivery();
        var now = T0.AddHours(1);

        delivery.RetryManually(now);

        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(now, delivery.NextAttemptAt);
        Assert.Null(delivery.CompletedAt);
        Assert.Equal(Policy.MaxAttempts, delivery.AttemptCount);
    }

    [Fact]
    public void A_manual_retry_that_succeeds_completes_the_delivery()
    {
        var delivery = DeadDelivery();
        delivery.RetryManually(T0.AddHours(1));

        delivery.RecordAttempt(Ok, Policy, 0.5, T0.AddHours(1));

        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(Policy.MaxAttempts + 1, delivery.AttemptCount);
    }

    [Fact]
    public void A_manual_retry_buys_exactly_one_more_attempt()
    {
        var delivery = DeadDelivery();
        delivery.RetryManually(T0.AddHours(1));

        delivery.RecordAttempt(ServerError, Policy, 0.5, T0.AddHours(1));

        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Succeeded)]
    public void Only_dead_deliveries_can_be_retried_manually(DeliveryStatus status)
    {
        var delivery = NewDelivery();
        if (status == DeliveryStatus.Failed)
        {
            delivery.RecordAttempt(ServerError, Policy, 0.5, T0);
        }
        else if (status == DeliveryStatus.Succeeded)
        {
            delivery.RecordAttempt(Ok, Policy, 0.5, T0);
        }

        Assert.Equal(status, delivery.Status);
        Assert.Throws<InvalidOperationException>(() => delivery.RetryManually(T0));
    }

    private static Delivery DeadDelivery()
    {
        var delivery = NewDelivery();
        for (var i = 0; i < Policy.MaxAttempts; i++)
        {
            delivery.RecordAttempt(ServerError, Policy, 0.5, T0);
        }

        Assert.Equal(DeliveryStatus.Dead, delivery.Status);
        return delivery;
    }
}
