using Carteiro.Dispatch;
using Microsoft.Extensions.Options;

namespace Carteiro;

/// <summary>
/// Everything configurable, bound from the "Carteiro" configuration section.
/// </summary>
public sealed class CarteiroOptions
{
    public const string SectionName = "Carteiro";

    /// <summary>
    /// Keys accepted in the X-Api-Key header. A list rather than a single value so a key can be
    /// rotated without downtime: add the new one, move clients over, remove the old one.
    /// </summary>
    public List<string> ApiKeys { get; set; } = [];

    public DatabaseOptions Database { get; set; } = new();

    public DeliveryOptions Delivery { get; set; } = new();

    public WorkerOptions Worker { get; set; } = new();
}

public sealed class DatabaseOptions
{
    /// <summary>
    /// Apply pending EF Core migrations when the host starts. Convenient for a single instance
    /// and for the docker compose demo; with several instances, run migrations as a deploy step.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}

public sealed class DeliveryOptions
{
    /// <summary>Attempts per delivery, the first one included, before it is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Backoff ceiling after the first failure. Doubles on each subsequent failure.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for the backoff.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a receiver has to answer before the attempt counts as failed.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Allow deliveries to loopback, private and link-local addresses. Off by default because it
    /// turns the service into an SSRF vector; needed for local development and docker networks.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>
    /// Disable an endpoint after this many consecutive failed attempts. Zero turns the feature off.
    /// </summary>
    public int EndpointFailureThreshold { get; set; } = 20;

    public RetryPolicy ToRetryPolicy() => new(MaxAttempts, BaseDelay, MaxDelay);
}

public sealed class WorkerOptions
{
    /// <summary>Run the delivery worker in this process. Turn off to run an API-only instance.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long the worker sleeps when it finds nothing to do.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum deliveries claimed, and sent concurrently, per polling round.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// How long a claimed delivery is hidden from other workers. Must be comfortably longer than
    /// <see cref="DeliveryOptions.RequestTimeout"/>; if a worker dies mid-request, this is how long
    /// the delivery waits before another worker picks it up.
    /// </summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromSeconds(60);
}

internal sealed class CarteiroOptionsValidator : IValidateOptions<CarteiroOptions>
{
    public ValidateOptionsResult Validate(string? name, CarteiroOptions options)
    {
        var errors = new List<string>();

        if (options.ApiKeys.Count == 0 || options.ApiKeys.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Carteiro:ApiKeys must contain at least one non-empty key.");
        }

        var delivery = options.Delivery;
        if (delivery.MaxAttempts < 1)
        {
            errors.Add("Carteiro:Delivery:MaxAttempts must be at least 1.");
        }

        if (delivery.BaseDelay <= TimeSpan.Zero)
        {
            errors.Add("Carteiro:Delivery:BaseDelay must be positive.");
        }

        if (delivery.MaxDelay < delivery.BaseDelay)
        {
            errors.Add("Carteiro:Delivery:MaxDelay must not be smaller than BaseDelay.");
        }

        if (delivery.RequestTimeout <= TimeSpan.Zero)
        {
            errors.Add("Carteiro:Delivery:RequestTimeout must be positive.");
        }

        if (delivery.EndpointFailureThreshold < 0)
        {
            errors.Add("Carteiro:Delivery:EndpointFailureThreshold must not be negative.");
        }

        var worker = options.Worker;
        if (worker.BatchSize < 1)
        {
            errors.Add("Carteiro:Worker:BatchSize must be at least 1.");
        }

        if (worker.PollInterval <= TimeSpan.Zero)
        {
            errors.Add("Carteiro:Worker:PollInterval must be positive.");
        }

        // If the lease could expire while a request is still in flight, a second worker would
        // pick the delivery up and the receiver would see a duplicate on every slow request.
        if (worker.ClaimLease <= delivery.RequestTimeout)
        {
            errors.Add("Carteiro:Worker:ClaimLease must be longer than Carteiro:Delivery:RequestTimeout.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
