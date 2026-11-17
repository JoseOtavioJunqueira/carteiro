using System.Text.Json;
using Carteiro.Domain;

namespace Carteiro.Api;

public sealed record CreateEndpointRequest(
    string? Url,
    List<string>? EventTypes,
    string? Description,
    string? Secret);

/// <summary>Partial update: only the fields that are present are changed.</summary>
public sealed record UpdateEndpointRequest(
    string? Url,
    List<string>? EventTypes,
    string? Description,
    bool? Enabled);

public record EndpointResponse(
    Guid Id,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool Enabled,
    int ConsecutiveFailures,
    DateTimeOffset? DisabledAt,
    string? DisabledReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static EndpointResponse From(WebhookEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.Url,
        endpoint.Description,
        endpoint.EventTypes,
        endpoint.Enabled,
        endpoint.ConsecutiveFailures,
        endpoint.DisabledAt,
        endpoint.DisabledReason,
        endpoint.CreatedAt,
        endpoint.UpdatedAt);
}

/// <summary>
/// Returned only when an endpoint is created. This is the one time the signing secret is shown.
/// </summary>
public sealed record EndpointCreatedResponse(
    Guid Id,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool Enabled,
    string Secret,
    DateTimeOffset CreatedAt);

public sealed record PublishEventRequest(string? Type, JsonElement? Payload);

public sealed record EventDeliverySummary(Guid Id, Guid EndpointId, DeliveryStatus Status, int AttemptCount);

public sealed record EventResponse(
    Guid Id,
    string Type,
    JsonElement Payload,
    string? IdempotencyKey,
    DateTimeOffset CreatedAt,
    IReadOnlyList<EventDeliverySummary> Deliveries)
{
    public static EventResponse From(Event @event, IEnumerable<Delivery> deliveries)
    {
        using var payload = JsonDocument.Parse(@event.Payload);

        return new EventResponse(
            @event.Id,
            @event.Type,
            payload.RootElement.Clone(),
            @event.IdempotencyKey,
            @event.CreatedAt,
            deliveries
                .OrderBy(d => d.Id)
                .Select(d => new EventDeliverySummary(d.Id, d.EndpointId, d.Status, d.AttemptCount))
                .ToList());
    }
}

public sealed record DeliveryAttemptResponse(
    int AttemptNumber,
    bool Succeeded,
    int? StatusCode,
    int DurationMs,
    string? Error,
    DateTimeOffset AttemptedAt);

public record DeliveryResponse(
    Guid Id,
    Guid EventId,
    Guid EndpointId,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static DeliveryResponse From(Delivery delivery) => new(
        delivery.Id,
        delivery.EventId,
        delivery.EndpointId,
        delivery.Status,
        delivery.AttemptCount,
        delivery.NextAttemptAt,
        delivery.LastError,
        delivery.CreatedAt,
        delivery.CompletedAt);
}

public sealed record DeliveryDetailResponse(
    Guid Id,
    Guid EventId,
    Guid EndpointId,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<DeliveryAttemptResponse> Attempts)
    : DeliveryResponse(Id, EventId, EndpointId, Status, AttemptCount, NextAttemptAt, LastError, CreatedAt, CompletedAt)
{
    public static new DeliveryDetailResponse From(Delivery delivery) => new(
        delivery.Id,
        delivery.EventId,
        delivery.EndpointId,
        delivery.Status,
        delivery.AttemptCount,
        delivery.NextAttemptAt,
        delivery.LastError,
        delivery.CreatedAt,
        delivery.CompletedAt,
        delivery.Attempts
            .OrderBy(a => a.AttemptNumber)
            .Select(a => new DeliveryAttemptResponse(a.AttemptNumber, a.Succeeded, a.StatusCode, a.DurationMs, a.Error, a.AttemptedAt))
            .ToList());
}
