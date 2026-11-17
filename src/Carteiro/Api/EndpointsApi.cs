using System.Security.Cryptography;
using Carteiro.Data;
using Carteiro.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Carteiro.Api;

internal static class EndpointsApi
{
    public static RouteGroupBuilder MapEndpointsApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/endpoints").WithTags("Endpoints");

        group.MapPost("/", Create)
            .WithName("CreateEndpoint")
            .WithSummary("Registers a webhook endpoint. The signing secret is only returned by this call.")
            .Produces<EndpointCreatedResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapGet("/", List)
            .WithName("ListEndpoints")
            .WithSummary("Lists all endpoints, newest first.")
            .Produces<List<EndpointResponse>>();

        group.MapGet("/{id:guid}", Get)
            .WithName("GetEndpoint")
            .Produces<EndpointResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPatch("/{id:guid}", Update)
            .WithName("UpdateEndpoint")
            .WithSummary("Changes the given fields. Setting enabled=true on a disabled endpoint resets its failure streak.")
            .Produces<EndpointResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteEndpoint")
            .WithSummary("Deletes an endpoint together with its deliveries and attempts.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> Create(
        CreateEndpointRequest request,
        CarteiroDbContext db,
        IOptions<CarteiroOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        RequestValidation.ValidateUrl(request.Url, options.Value.Delivery.AllowPrivateNetworks, errors);
        RequestValidation.ValidateEventTypes(request.EventTypes, errors);
        RequestValidation.ValidateDescription(request.Description, errors);
        RequestValidation.ValidateSecret(request.Secret, errors);
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var endpoint = WebhookEndpoint.Create(
            request.Url!,
            request.Secret ?? GenerateSecret(),
            request.EventTypes!,
            request.Description,
            time.GetUtcNow());

        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);

        var response = new EndpointCreatedResponse(
            endpoint.Id,
            endpoint.Url,
            endpoint.Description,
            endpoint.EventTypes,
            endpoint.Enabled,
            endpoint.Secret,
            endpoint.CreatedAt);

        return Results.Created($"/endpoints/{endpoint.Id}", response);
    }

    private static async Task<IResult> List(CarteiroDbContext db, CancellationToken cancellationToken)
    {
        var endpoints = await db.Endpoints
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .ToListAsync(cancellationToken);

        return Results.Ok(endpoints.Select(EndpointResponse.From));
    }

    private static async Task<IResult> Get(Guid id, CarteiroDbContext db, CancellationToken cancellationToken)
    {
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

        return endpoint is null ? NotFound(id) : Results.Ok(EndpointResponse.From(endpoint));
    }

    private static async Task<IResult> Update(
        Guid id,
        UpdateEndpointRequest request,
        CarteiroDbContext db,
        IOptions<CarteiroOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Endpoints.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (endpoint is null)
        {
            return NotFound(id);
        }

        var url = request.Url ?? endpoint.Url;
        var eventTypes = request.EventTypes ?? endpoint.EventTypes;
        var description = request.Description ?? endpoint.Description;

        var errors = new ValidationErrors();
        if (request.Url is not null)
        {
            RequestValidation.ValidateUrl(url, options.Value.Delivery.AllowPrivateNetworks, errors);
        }

        RequestValidation.ValidateEventTypes(eventTypes, errors);
        RequestValidation.ValidateDescription(description, errors);
        if (errors.Any)
        {
            return errors.ToProblem();
        }

        var now = time.GetUtcNow();
        endpoint.Update(url, eventTypes, description, now);

        if (request.Enabled == true)
        {
            endpoint.Enable(now);
        }
        else if (request.Enabled == false)
        {
            endpoint.Disable("Disabled through the API.", now);
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(EndpointResponse.From(endpoint));
    }

    private static async Task<IResult> Delete(Guid id, CarteiroDbContext db, CancellationToken cancellationToken)
    {
        // Deliveries and attempts go with it through ON DELETE CASCADE.
        var deleted = await db.Endpoints.Where(e => e.Id == id).ExecuteDeleteAsync(cancellationToken);

        return deleted == 0 ? NotFound(id) : Results.NoContent();
    }

    private static IResult NotFound(Guid id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Endpoint {id} does not exist.");

    private static string GenerateSecret() =>
        "whsec_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
