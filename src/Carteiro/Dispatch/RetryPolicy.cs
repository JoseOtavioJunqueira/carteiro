namespace Carteiro.Dispatch;

/// <summary>
/// What to do with a delivery after a failed attempt.
/// </summary>
public readonly record struct RetryDecision
{
    private RetryDecision(bool shouldRetry, TimeSpan delay)
    {
        ShouldRetry = shouldRetry;
        Delay = delay;
    }

    public bool ShouldRetry { get; }

    /// <summary>How long to wait before the next attempt. Zero when <see cref="ShouldRetry"/> is false.</summary>
    public TimeSpan Delay { get; }

    public static RetryDecision GiveUp { get; } = new(false, TimeSpan.Zero);

    public static RetryDecision RetryAfter(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        return new RetryDecision(true, delay);
    }
}

/// <summary>
/// Exponential backoff with "equal jitter": the delay for attempt n is drawn from
/// [ceiling/2, ceiling), where ceiling = min(MaxDelay, BaseDelay * 2^(n-1)).
/// </summary>
/// <remarks>
/// The policy is a pure function. The random sample is passed in by the caller, so the same
/// inputs always produce the same decision and the whole thing can be tested without a clock
/// or a random number generator.
/// </remarks>
public sealed record RetryPolicy
{
    public RetryPolicy(int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, baseDelay);

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        MaxDelay = maxDelay;
    }

    /// <summary>Total number of attempts (the first one included) before a delivery is dead-lettered.</summary>
    public int MaxAttempts { get; }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Decides what happens after a failed attempt.
    /// </summary>
    /// <param name="attemptsMade">Attempts made so far, including the one that just failed (1-based).</param>
    /// <param name="jitterSample">A uniform random sample in [0, 1).</param>
    public RetryDecision Next(int attemptsMade, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);
        if (jitterSample is < 0.0 or >= 1.0 || double.IsNaN(jitterSample))
        {
            throw new ArgumentOutOfRangeException(nameof(jitterSample), jitterSample, "Jitter sample must be in [0, 1).");
        }

        if (attemptsMade >= MaxAttempts)
        {
            return RetryDecision.GiveUp;
        }

        var ceiling = BackoffCeiling(attemptsMade);
        var half = ceiling.Ticks / 2;
        var jittered = half + (long)(half * jitterSample);
        return RetryDecision.RetryAfter(TimeSpan.FromTicks(jittered));
    }

    /// <summary>
    /// The un-jittered backoff for the given attempt: BaseDelay * 2^(attemptsMade - 1), capped at MaxDelay.
    /// </summary>
    public TimeSpan BackoffCeiling(int attemptsMade)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsMade, 1);

        // Doubling in a loop instead of Math.Pow keeps this exact and makes overflow impossible:
        // we stop as soon as the cap is reached.
        var delay = BaseDelay;
        for (var i = 1; i < attemptsMade; i++)
        {
            if (delay >= MaxDelay / 2)
            {
                return MaxDelay;
            }

            delay *= 2;
        }

        return delay < MaxDelay ? delay : MaxDelay;
    }
}
