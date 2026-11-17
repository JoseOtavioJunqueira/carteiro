using System.Text;
using System.Text.Json;
using Carteiro.Data;
using Carteiro.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Carteiro.Api;

internal static partial class EventsApi
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static RouteGroupBuilder MapEventsApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/events").WithTags("Events");

        group.MapPost("/", Publish)
            .WithName("PublishEvent")
            .WithSummary("Accepts an event and creates one delivery per subscribed endpoint, atomically.")
            .WithDescription(
                "Send an Idempotency-Key header to make retries safe: a repeated key returns the original event " +
                "(200, with Idempotent-Replayed: true) instead of creating a new one.")
            .Produces<EventResponse>(StatusCodes.Status201Created)
            .Produces<EventResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}", Get)
            .WithName("GetEvent")
            .Produces<EventResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> Publish(
        PublishEventRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        HttpContext httpContext,
        CarteiroDbContext db,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();

        if (!RequestValidation.IsValidEventType(request.Type))
        {
            errors.Add("type", "The type is required. Use letters, digits, '.', '_', ':' or '-' (max 100 characters).");
        }

        string? payload = null;
        if (request.Payload is not { ValueKind: JsonValueKind.Object } payloadElement)
        {
            errors.Add("payload", "The payload is required and must be a JSON object.");
        }
        else
        {
            // GetRawText returns the producer's bytes untouched. That exact text is what gets
            // stored, signed and delivered.
            payload = payloadElement.GetRawText();
            if (Encoding.UTF8.GetByteCount(payload) > RequestValidation.MaxPayloadBytes)
            {
                errors.Add("payload", $"The payload must be at most {RequestValidation.MaxPayloadBytes / 1024} KB.");
            }
        }

        if (idempotencyKey is not null &&
            (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > RequestValidation.MaxIdempotencyKeyLength))
        {
            errors.Add(IdempotencyKeyHeader, $"The idempotency key must be between 1 and {RequestValidation.MaxIdempotencyKeyLength} characters.");
        }

        if (errors.Any)
        {
            return errors.ToProblem();
        }

        // Fast path for retries. This check alone would not be enough: two concurrent requests
        // can both find nothing here. The unique index below is what settles that race.
        if (idempotencyKey is not null &&
            await FindByIdempotencyKey(db, idempotencyKey, cancellationToken) is { } existing)
        {
            return Replay(httpContext, existing.Event, existing.Deliveries, request.Type!, payload!);
        }

        var now = time.GetUtcNow();
        var @event = Event.Create(request.Type!, payload!, idempotencyKey, now);

        var subscribedEndpointIds = await db.Endpoints
            .Where(e => e.Enabled &&
                (e.EventTypes.Contains(@event.Type) || e.EventTypes.Contains(WebhookEndpoint.AllEventTypes)))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var deliveries = subscribedEndpointIds.Select(endpointId => Delivery.Create(@event.Id, endpointId, now)).ToList();

        db.Events.Add(@event);
        db.Deliveries.AddRange(deliveries);

        try
        {
            // One SaveChanges is one database transaction: the event and all of its deliveries
            // are committed together or not at all. There is no window in which an accepted
            // event exists without its deliveries, and nothing to publish to a broker afterwards.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null && IsIdempotencyKeyConflict(ex))
        {
            // Lost the race against a concurrent request with the same key. Nothing of ours was
            // committed; return the winner's event.
            db.ChangeTracker.Clear();
            var winner = await FindByIdempotencyKey(db, idempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency key conflict reported, but no event holds the key.", ex);

            return Replay(httpContext, winner.Event, winner.Deliveries, request.Type!, payload!);
        }

        Log.EventAccepted(loggerFactory.CreateLogger(typeof(EventsApi)), @event.Id, @event.Type, deliveries.Count);

        return Results.Created($"/events/{@event.Id}", EventResponse.From(@event, deliveries));
    }

    private static async Task<IResult> Get(Guid id, CarteiroDbContext db, CancellationToken cancellationToken)
    {
        var @event = await db.Events.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (@event is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Event {id} does not exist.");
        }

        var deliveries = await db.Deliveries.AsNoTracking().Where(d => d.EventId == id).ToListAsync(cancellationToken);

        return Results.Ok(EventResponse.From(@event, deliveries));
    }

    private static IResult Replay(HttpContext httpContext, Event existing, List<Delivery> deliveries, string type, string payload)
    {
        // Same key, different request: almost certainly a client bug. Returning the old event
        // would hide it, so refuse instead.
        if (existing.Type != type || existing.Payload != payload)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency key reuse",
                detail: $"This {IdempotencyKeyHeader} was already used for event {existing.Id} with a different type or payload.");
        }

        httpContext.Response.Headers[ReplayedHeader] = "true";
        return Results.Ok(EventResponse.From(existing, deliveries));
    }

    private static async Task<(Event Event, List<Delivery> Deliveries)?> FindByIdempotencyKey(
        CarteiroDbContext db,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var @event = await db.Events.AsNoTracking()
            .SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, cancellationToken);
        if (@event is null)
        {
            return null;
        }

        var deliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.EventId == @event.Id)
            .ToListAsync(cancellationToken);

        return (@event, deliveries);
    }

    private static bool IsIdempotencyKeyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_events_idempotency_key",
        };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Accepted event {EventId} of type {EventType} with {DeliveryCount} deliveries")]
        public static partial void EventAccepted(ILogger logger, Guid eventId, string eventType, int deliveryCount);
    }
}
