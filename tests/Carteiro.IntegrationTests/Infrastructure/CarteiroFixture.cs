using System.Net.Http.Headers;
using Carteiro.Api;
using Carteiro.Data;
using Carteiro.Dispatch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Carteiro.IntegrationTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<CarteiroFixture>
{
    public const string Name = "Integration";
}

/// <summary>
/// One PostgreSQL container and one application host shared by all integration tests.
/// </summary>
/// <remarks>
/// The background worker is switched off: tests drive <see cref="DeliveryProcessor"/> directly so
/// they decide exactly when deliveries are attempted. Time is a <see cref="FakeTimeProvider"/>,
/// so backoff can be skipped by advancing the clock instead of sleeping.
/// </remarks>
public sealed class CarteiroFixture : IAsyncLifetime
{
    public const string ApiKey = "integration-test-key";

    public const int MaxAttempts = 3;
    public const int EndpointFailureThreshold = 5;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    // Factories created with WithWebHostBuilder are owned, and disposed, by the one they came from.
    private readonly WebApplicationFactory<Program> _root = new();

    public FakeReceiver Receiver { get; } = new();

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public IServiceProvider Services => Factory.Services;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = CreateFactory();

        // Building the host runs the migrations (MigrateOnStartup).
        _ = Factory.Services;
    }

    public async Task DisposeAsync()
    {
        await _root.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A host with the shared configuration, optionally overriding some settings.</summary>
    public WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] overrides)
    {
        return _root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Carteiro", _postgres.GetConnectionString());
            builder.UseSetting("Carteiro:ApiKeys:0", ApiKey);
            builder.UseSetting("Carteiro:Database:MigrateOnStartup", "true");
            builder.UseSetting("Carteiro:Worker:Enabled", "false");
            builder.UseSetting("Carteiro:Worker:BatchSize", "10");
            builder.UseSetting("Carteiro:Worker:ClaimLease", ClaimLease.ToString());
            builder.UseSetting("Carteiro:Delivery:MaxAttempts", MaxAttempts.ToString());
            builder.UseSetting("Carteiro:Delivery:BaseDelay", "00:00:10");
            builder.UseSetting("Carteiro:Delivery:MaxDelay", MaxDelay.ToString());
            builder.UseSetting("Carteiro:Delivery:RequestTimeout", "00:00:05");
            builder.UseSetting("Carteiro:Delivery:EndpointFailureThreshold", EndpointFailureThreshold.ToString());
            builder.UseSetting("Carteiro:Delivery:AllowPrivateNetworks", "true");

            foreach (var (key, value) in overrides)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Time);
                services.AddHttpClient(WebhookSender.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeReceiverHandler(Receiver));
            });
        });
    }

    public HttpClient CreateClient(WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? Factory).CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyFilter.HeaderName, ApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>A delivery processor resolved from the shared host.</summary>
    internal DeliveryProcessor Processor => Services.GetRequiredService<DeliveryProcessor>();

    /// <summary>
    /// A second, independent processor instance against the same database: what another worker
    /// process would be.
    /// </summary>
    internal DeliveryProcessor NewProcessor() => ActivatorUtilities.CreateInstance<DeliveryProcessor>(Services);

    public CarteiroDbContext CreateDbContext() =>
        Services.GetRequiredService<IDbContextFactory<CarteiroDbContext>>().CreateDbContext();

    /// <summary>Empties every table and resets fakes. Called before each test.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE endpoints, events, deliveries, delivery_attempts");

        Receiver.Reset();

        // The clock is not reset: FakeTimeProvider only moves forward, and no test depends on
        // absolute time, only on how much it advances.
    }

    /// <summary>Runs processor rounds until nothing due is left.</summary>
    internal async Task DrainAsync()
    {
        while (await Processor.RunOnceAsync(CancellationToken.None) > 0)
        {
        }
    }
}

/// <summary>Base class: shares the fixture and starts every test from an empty database.</summary>
[Collection(IntegrationCollection.Name)]
public abstract class IntegrationTest(CarteiroFixture fixture) : IAsyncLifetime
{
    protected CarteiroFixture Fixture { get; } = fixture;

    protected HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Fixture.ResetAsync();
        Client = Fixture.CreateClient();
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }
}
