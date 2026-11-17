using System.Text.Json.Serialization;
using Carteiro;
using Carteiro.Api;
using Carteiro.Data;
using Carteiro.Dispatch;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<CarteiroOptions>()
    .BindConfiguration(CarteiroOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CarteiroOptions>, CarteiroOptionsValidator>();

builder.Services.AddSingleton(TimeProvider.System);

// The factory is what the background worker uses (it has no request scope); registering it also
// makes a scoped CarteiroDbContext available to the HTTP endpoints.
builder.Services.AddDbContextFactory<CarteiroDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Carteiro")
        ?? throw new InvalidOperationException("Connection string 'Carteiro' is not configured.");

    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
});

builder.Services
    .AddHttpClient(WebhookSender.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(services =>
        WebhookHttpHandler.Create(services.GetRequiredService<IOptions<CarteiroOptions>>().Value.Delivery))
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan); // WebhookSender applies its own timeout
builder.Services.AddSingleton<WebhookSender>();
builder.Services.AddSingleton<DeliveryMetrics>();
builder.Services.AddSingleton<DeliveryProcessor>();
builder.Services.AddHostedService<DeliveryWorker>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CarteiroDbContext>("database", tags: ["ready"]);

builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>());

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<CarteiroOptions>>().Value.Database.MigrateOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<CarteiroDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

// Unauthenticated on purpose: orchestrators and load balancers call these.
// Liveness says the process is up; readiness also checks the database.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// The OpenAPI document describes the API but contains no data, so it is public too.
app.MapOpenApi("/openapi/{documentName}.json");

var api = app.MapGroup(string.Empty).AddEndpointFilter<ApiKeyFilter>();
api.MapEndpointsApi();
api.MapEventsApi();
api.MapDeliveriesApi();

app.Run();

public partial class Program;
