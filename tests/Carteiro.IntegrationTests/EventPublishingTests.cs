using System.Net;
using Carteiro.Api;
using Carteiro.Domain;
using Carteiro.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Carteiro.IntegrationTests;

public class EventPublishingTests(CarteiroFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task An_event_fans_out_to_every_enabled_subscribed_endpoint()
    {
        var exact = await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        var wildcard = await Client.CreateEndpointAsync("https://b.example/hooks", "*");
        await Client.CreateEndpointAsync("https://c.example/hooks", "order.paid");
        var disabled = await Client.CreateEndpointAsync("https://d.example/hooks", "order.created");
        await Client.PatchAsync($"/endpoints/{disabled.Id}", JsonContent("""{"enabled":false}"""));

        var @event = await Client.PublishOkAsync("order.created", """{"orderId":42}""");

        Assert.Equal(
            new[] { exact.Id, wildcard.Id }.Order(),
            @event.Deliveries.Select(d => d.EndpointId).Order());
        Assert.All(@event.Deliveries, d => Assert.Equal(DeliveryStatus.Pending, d.Status));
    }

    [Fact]
    public async Task An_event_without_subscribers_is_accepted_with_no_deliveries()
    {
        var @event = await Client.PublishOkAsync("nobody.cares", "{}");

        Assert.Empty(@event.Deliveries);
    }

    [Fact]
    public async Task The_payload_is_stored_and_delivered_byte_for_byte()
    {
        await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        const string payload = """{ "b" : 2,   "a":1, "nome":"José" }""";

        await Client.PublishOkAsync("order.created", payload);
        await Fixture.DrainAsync();

        Assert.Equal(payload, Assert.Single(Fixture.Receiver.Received).Body);
    }

    [Theory]
    [InlineData("""{"payload":{}}""")]
    [InlineData("""{"type":"order.created"}""")]
    [InlineData("""{"type":"order.created","payload":[1,2]}""")]
    [InlineData("""{"type":"has spaces","payload":{}}""")]
    public async Task Invalid_events_are_rejected(string body)
    {
        var response = await Client.PostAsync("/events", JsonContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_event_and_its_deliveries_are_committed_atomically()
    {
        var healthy = await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        var poisoned = await Client.CreateEndpointAsync("https://b.example/hooks", "order.created");

        // Make the database reject the delivery row for one endpoint. If the event insert were
        // committed separately from its deliveries, the event and the healthy delivery would
        // survive this failure.
        await using (var db = Fixture.CreateDbContext())
        {
            // DDL cannot take parameters; the only interpolated value is a Guid we generated.
            var createTrigger = $"""
                CREATE OR REPLACE FUNCTION reject_poisoned_delivery() RETURNS trigger AS $$
                BEGIN
                    IF NEW.endpoint_id = '{poisoned.Id}' THEN
                        RAISE EXCEPTION 'simulated failure while inserting a delivery';
                    END IF;
                    RETURN NEW;
                END $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_poisoned_delivery BEFORE INSERT ON deliveries
                    FOR EACH ROW EXECUTE FUNCTION reject_poisoned_delivery();
                """;
            await db.Database.ExecuteSqlRawAsync(createTrigger);
        }

        try
        {
            var response = await Client.PublishAsync("order.created", """{"orderId":1}""");
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            await using var db = Fixture.CreateDbContext();
            Assert.Equal(0, await db.Events.CountAsync());
            Assert.Equal(0, await db.Deliveries.CountAsync(d => d.EndpointId == healthy.Id));
        }
        finally
        {
            await using var db = Fixture.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER reject_poisoned_delivery ON deliveries;
                DROP FUNCTION reject_poisoned_delivery();
                """);
        }
    }

    [Fact]
    public async Task An_event_can_be_fetched_with_its_deliveries()
    {
        await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        var published = await Client.PublishOkAsync("order.created", """{"orderId":7}""");

        var fetched = await Client.GetOkAsync<EventResponse>($"/events/{published.Id}");

        Assert.Equal(published.Id, fetched.Id);
        Assert.Equal(7, fetched.Payload.GetProperty("orderId").GetInt32());
        Assert.Single(fetched.Deliveries);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/events/{Guid.NewGuid()}")).StatusCode);
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}

public class IdempotencyTests(CarteiroFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Repeating_a_request_with_the_same_key_returns_the_original_event()
    {
        await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        await Client.CreateEndpointAsync("https://b.example/hooks", "order.created");

        var first = await Client.PublishAsync("order.created", """{"orderId":1}""", idempotencyKey: "order-1-created");
        var second = await Client.PublishAsync("order.created", """{"orderId":1}""", idempotencyKey: "order-1-created");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());

        var original = await first.ReadAsync<EventResponse>();
        var replayed = await second.ReadAsync<EventResponse>();
        Assert.Equal(original.Id, replayed.Id);
        Assert.Equal(original.Deliveries.Select(d => d.Id), replayed.Deliveries.Select(d => d.Id));

        await using var db = Fixture.CreateDbContext();
        Assert.Equal(1, await db.Events.CountAsync());
        Assert.Equal(2, await db.Deliveries.CountAsync());
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_exactly_one_event()
    {
        await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");

        // Start all requests together so several of them pass the "does this key exist?" check
        // before any has committed. Only the unique index can sort that out.
        using var start = new ManualResetEventSlim();
        var requests = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            using var client = Fixture.CreateClient();
            return await client.PublishAsync("order.created", """{"orderId":99}""", idempotencyKey: "race");
        })).ToList();

        start.Set();
        var responses = await Task.WhenAll(requests);

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"Unexpected {r.StatusCode}"));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);

        var ids = await Task.WhenAll(responses.Select(async r => (await r.ReadAsync<EventResponse>()).Id));
        Assert.Single(ids.Distinct());

        await using var db = Fixture.CreateDbContext();
        Assert.Equal(1, await db.Events.CountAsync());
        Assert.Equal(1, await db.Deliveries.CountAsync());
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_payload_is_refused()
    {
        await Client.PublishOkAsync("order.created", """{"orderId":1}""", idempotencyKey: "k");

        var response = await Client.PublishAsync("order.created", """{"orderId":2}""", idempotencyKey: "k");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_key_are_never_deduplicated()
    {
        var first = await Client.PublishOkAsync("order.created", """{"orderId":1}""");
        var second = await Client.PublishOkAsync("order.created", """{"orderId":1}""");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task The_database_enforces_key_uniqueness()
    {
        await using var db = Fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.Events.Add(Event.Create("a.b", "{}", "dup", now));
        db.Events.Add(Event.Create("a.b", "{}", "dup", now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Keys_longer_than_the_limit_are_rejected()
    {
        var response = await Client.PublishAsync("order.created", "{}", idempotencyKey: new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Idempotency-Key", await response.Content.ReadAsStringAsync());
    }
}
