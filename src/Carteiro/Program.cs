using System.Text.Json.Serialization;
using Carteiro;
using Carteiro.Api;
using Carteiro.Data;
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

var api = app.MapGroup(string.Empty).AddEndpointFilter<ApiKeyFilter>();
api.MapEndpointsApi();

app.Run();

public partial class Program;
