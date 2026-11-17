using System.Diagnostics.Metrics;
using Carteiro.Domain;

namespace Carteiro.Dispatch;

/// <summary>
/// Delivery metrics, published through <see cref="System.Diagnostics.Metrics"/> under the meter
/// name "Carteiro". Nothing is exported by default; attach OpenTelemetry or watch them live with
/// <c>dotnet-counters monitor -n Carteiro --counters Carteiro</c>.
/// </summary>
internal sealed class DeliveryMetrics
{
    public const string MeterName = "Carteiro";

    private readonly Counter<long> _attempts;
    private readonly Counter<long> _deadLettered;
    private readonly Histogram<double> _attemptDuration;
    private readonly Counter<long> _endpointsDisabled;

    public DeliveryMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _attempts = meter.CreateCounter<long>(
            "carteiro.delivery.attempts",
            unit: "{attempt}",
            description: "Delivery attempts made, tagged with outcome=succeeded|failed.");

        _deadLettered = meter.CreateCounter<long>(
            "carteiro.delivery.dead_lettered",
            unit: "{delivery}",
            description: "Deliveries that exhausted their retries.");

        _attemptDuration = meter.CreateHistogram<double>(
            "carteiro.delivery.attempt.duration",
            unit: "ms",
            description: "Time from sending the request to receiving response headers, or to giving up.");

        _endpointsDisabled = meter.CreateCounter<long>(
            "carteiro.endpoint.auto_disabled",
            unit: "{endpoint}",
            description: "Endpoints disabled automatically after too many consecutive failures.");
    }

    public void AttemptRecorded(AttemptOutcome outcome, DeliveryStatus statusAfter)
    {
        var tag = new KeyValuePair<string, object?>("outcome", outcome.Succeeded ? "succeeded" : "failed");

        _attempts.Add(1, tag);
        _attemptDuration.Record(outcome.Duration.TotalMilliseconds, tag);

        if (statusAfter == DeliveryStatus.Dead)
        {
            _deadLettered.Add(1);
        }
    }

    public void EndpointDisabled() => _endpointsDisabled.Add(1);
}
