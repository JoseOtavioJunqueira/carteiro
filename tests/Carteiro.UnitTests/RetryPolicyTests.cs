using Carteiro.Dispatch;

namespace Carteiro.UnitTests;

public class RetryPolicyTests
{
    private static readonly RetryPolicy Policy = new(
        maxAttempts: 5,
        baseDelay: TimeSpan.FromSeconds(10),
        maxDelay: TimeSpan.FromMinutes(1));

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)] // 80s capped to the 60s maximum
    [InlineData(30, 60)]
    public void Backoff_ceiling_doubles_per_attempt_and_is_capped(int attemptsMade, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Policy.BackoffCeiling(attemptsMade));
    }

    [Fact]
    public void Backoff_ceiling_does_not_overflow_for_absurd_attempt_counts()
    {
        var policy = new RetryPolicy(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromDays(365));

        Assert.Equal(TimeSpan.FromDays(365), policy.BackoffCeiling(10_000));
    }

    [Fact]
    public void Lowest_jitter_sample_waits_half_of_the_ceiling()
    {
        var decision = Policy.Next(attemptsMade: 2, jitterSample: 0.0);

        Assert.True(decision.ShouldRetry);
        Assert.Equal(TimeSpan.FromSeconds(10), decision.Delay);
    }

    [Fact]
    public void Jitter_is_linear_between_half_and_full_ceiling()
    {
        var decision = Policy.Next(attemptsMade: 2, jitterSample: 0.5);

        Assert.Equal(TimeSpan.FromSeconds(15), decision.Delay);
    }

    [Fact]
    public void Delay_always_stays_within_half_and_full_ceiling()
    {
        var random = new Random(1234);

        for (var attempt = 1; attempt < Policy.MaxAttempts; attempt++)
        {
            var ceiling = Policy.BackoffCeiling(attempt);

            for (var i = 0; i < 1_000; i++)
            {
                var decision = Policy.Next(attempt, random.NextDouble());

                Assert.True(decision.ShouldRetry);
                Assert.InRange(decision.Delay, ceiling / 2, ceiling);
            }
        }
    }

    [Fact]
    public void Same_inputs_give_the_same_decision()
    {
        Assert.Equal(Policy.Next(3, 0.42), Policy.Next(3, 0.42));
    }

    [Fact]
    public void Retries_until_the_last_allowed_attempt()
    {
        Assert.True(Policy.Next(attemptsMade: 4, jitterSample: 0.3).ShouldRetry);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(100)]
    public void Gives_up_once_max_attempts_is_reached(int attemptsMade)
    {
        var decision = Policy.Next(attemptsMade, jitterSample: 0.3);

        Assert.False(decision.ShouldRetry);
        Assert.Equal(TimeSpan.Zero, decision.Delay);
    }

    [Fact]
    public void A_single_attempt_policy_never_retries()
    {
        var policy = new RetryPolicy(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.False(policy.Next(1, 0.0).ShouldRetry);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void Rejects_jitter_samples_outside_the_unit_interval(double sample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.Next(1, sample));
    }

    [Fact]
    public void Rejects_attempt_numbers_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.Next(0, 0.5));
    }

    [Fact]
    public void Rejects_invalid_configuration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, TimeSpan.Zero, TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
    }
}
