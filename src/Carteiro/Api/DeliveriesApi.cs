using Carteiro.Data;
using Carteiro.Domain;
using Microsoft.EntityFrameworkCore;

namespace Carteiro.Api;

internal static class DeliveriesApi
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static RouteGroupBuilder MapDeliveriesApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/deliveries").WithTags("Deliveries");

        group.MapGet("/", List)
            .WithName("ListDeliveries")
            .WithSummary("Lists deliveries, newest first. Filter by status, endpointId and eventId; page with limit and offset.")
            .Produces<List<DeliveryResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", Get)
            .WithName("GetDelivery")
            .WithSummary("Returns one delivery with every attempt made for it.")
            .Produces<DeliveryDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/retry", Retry)
            .WithName("RetryDelivery")
            .WithSummary("Puts a dead delivery back in the queue for one more attempt.")
            .Produces<DeliveryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<IResult> List(
        CarteiroDbContext db,
        CancellationToken cancellationToken,
        DeliveryStatus? status = null,
        Guid? endpointId = null,
        Guid? eventId = null,
        int limit = DefaultLimit,
        int offset = 0)
    {
        var errors = new ValidationErrors();
        if (limit is < 1 or > MaxLimit)
        {
            errors.Add("limit", $"The limit must be between 1 and {MaxLimit}.");
        }

        if (offset < 0)
        {
            errors.Add("offset", "The offset must not be negative.");
        }

        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var query = db.Deliveries.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(d => d.Status == status);
        }

        if (endpointId is not null)
        {
            query = query.Where(d => d.EndpointId == endpointId);
        }

        if (eventId is not null)
        {
            query = query.Where(d => d.EventId == eventId);
        }

        var deliveries = await query
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return Results.Ok(deliveries.Select(DeliveryResponse.From));
    }

    private static async Task<IResult> Get(Guid id, CarteiroDbContext db, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries
            .AsNoTracking()
            .Include(d => d.Attempts)
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

        return delivery is null ? NotFound(id) : Results.Ok(DeliveryDetailResponse.From(delivery));
    }

    private static async Task<IResult> Retry(Guid id, CarteiroDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var delivery = await db.Deliveries
            .Include(d => d.Endpoint)
            .SingleOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (delivery is null)
        {
            return NotFound(id);
        }

        if (delivery.Status != DeliveryStatus.Dead)
        {
            return Conflict($"Only dead deliveries can be retried; this one is {delivery.Status}.");
        }

        // The worker skips deliveries of disabled endpoints, so re-queueing would look like it
        // worked while nothing happens. Say so instead.
        if (delivery.Endpoint is { Enabled: false })
        {
            return Conflict($"Endpoint {delivery.EndpointId} is disabled. Enable it before retrying.");
        }

        delivery.RetryManually(time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(DeliveryResponse.From(delivery));
    }

    private static IResult NotFound(Guid id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Delivery {id} does not exist.");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail);
}
