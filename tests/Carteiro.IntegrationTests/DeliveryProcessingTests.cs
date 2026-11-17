using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Carteiro.Api;
using Carteiro.Dispatch;
using Carteiro.Domain;
using Carteiro.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Carteiro.IntegrationTests;

public class DeliveryProcessingTests(CarteiroFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task A_delivery_is_sent_signed_and_marked_succeeded()
    {
        var endpoint = await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        var @event = await Client.PublishOkAsync("order.created", """{"orderId":42}""");
        var deliveryId = Assert.Single(@event.Deliveries).Id;

        await Fixture.DrainAsync();

        var received = Assert.Single(Fixture.Receiver.Received);
        Assert.Equal("https://receiver.example/hooks", received.Url);
        Assert.Equal("""{"orderId":42}""", received.Body);
        Assert.Equal(deliveryId.ToString(), received.Header(WebhookHeaders.DeliveryId));
        Assert.Equal(@event.Id.ToString(), received.Header(WebhookHeaders.EventId));
        Assert.Equal("order.created", received.Header(WebhookHeaders.EventType));
        Assert.Equal("1", received.Header(WebhookHeaders.Attempt));

        // Verify exactly as a receiver would: with the secret it was given and the raw body.
        var timestamp = long.Parse(received.Header(WebhookHeaders.Timestamp), CultureInfo.InvariantCulture);
        Assert.True(WebhookSigner.Verify(
            endpoint.Secret, timestamp, received.Body, received.Header(WebhookHeaders.Signature),
            Fixture.Time.GetUtcNow(), TimeSpan.FromMinutes(5)));

        var delivery = await Client.GetOkAsync<DeliveryDetailResponse>($"/deliveries/{deliveryId}");
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.NotNull(delivery.CompletedAt);
        Assert.Null(delivery.NextAttemptAt);
        var attempt = Assert.Single(delivery.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal(200, attempt.StatusCode);
    }

    [Fact]
    public async Task Any_2xx_counts_as_success()
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        Fixture.Receiver.RespondWith(HttpStatusCode.NoContent);

        var @event = await Client.PublishOkAsync("order.created", "{}");
        await Fixture.DrainAsync();

        var delivery = await Client.GetOkAsync<DeliveryResponse>($"/deliveries/{@event.Deliveries[0].Id}");
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Moved)] // redirects are not followed
    public async Task A_non_2xx_answer_is_a_failure_and_is_retried_later(HttpStatusCode statusCode)
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        Fixture.Receiver.RespondWith(statusCode);
        var @event = await Client.PublishOkAsync("order.created", "{}");
        var before = Fixture.Time.GetUtcNow();

        await Fixture.DrainAsync();

        var delivery = await Client.GetOkAsync<DeliveryDetailResponse>($"/deliveries/{@event.Deliveries[0].Id}");
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal((int)statusCode, Assert.Single(delivery.Attempts).StatusCode);
        Assert.Contains(((int)statusCode).ToString(CultureInfo.InvariantCulture), delivery.LastError);

        // First backoff ceiling is BaseDelay (10s); with jitter the wait is in [5s, 10s].
        Assert.InRange(delivery.NextAttemptAt!.Value, before.AddSeconds(5), before.AddSeconds(10));

        // Not due yet: another round must not touch it.
        Assert.Equal(0, await Fixture.Processor.RunOnceAsync(CancellationToken.None));
        Assert.Single(Fixture.Receiver.Received);
    }

    [Fact]
    public async Task A_timeout_is_a_failure_without_status_code()
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        Fixture.Receiver.RespondWith(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return HttpStatusCode.OK;
        });

        using var shortTimeout = Fixture.CreateFactory(("Carteiro:Delivery:RequestTimeout", "00:00:00.300"));
        using var client = Fixture.CreateClient(shortTimeout);
        var @event = await client.PublishOkAsync("order.created", "{}");

        var processor = shortTimeout.Services.GetRequiredService<DeliveryProcessor>();
        await processor.RunOnceAsync(CancellationToken.None);

        var delivery = await client.GetOkAsync<DeliveryDetailResponse>($"/deliveries/{@event.Deliveries[0].Id}");
        var attempt = Assert.Single(delivery.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.Null(attempt.StatusCode);
        Assert.Contains("No response within", attempt.Error);
    }

    [Fact]
    public async Task A_delivery_retries_with_backoff_then_goes_dead_and_can_be_retried_manually()
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        Fixture.Receiver.RespondWith(HttpStatusCode.ServiceUnavailable);
        var @event = await Client.PublishOkAsync("order.created", "{}");
        var deliveryId = @event.Deliveries[0].Id;

        for (var i = 0; i < CarteiroFixture.MaxAttempts; i++)
        {
            await Fixture.DrainAsync();
            Fixture.Time.Advance(CarteiroFixture.MaxDelay);
        }

        var dead = await Client.GetOkAsync<DeliveryDetailResponse>($"/deliveries/{deliveryId}");
        Assert.Equal(DeliveryStatus.Dead, dead.Status);
        Assert.Equal(CarteiroFixture.MaxAttempts, dead.AttemptCount);
        Assert.Equal([1, 2, 3], dead.Attempts.Select(a => a.AttemptNumber));
        Assert.Null(dead.NextAttemptAt);
        Assert.Equal(["1", "2", "3"], Fixture.Receiver.Received.Select(r => r.Header(WebhookHeaders.Attempt)));

        // A dead delivery is never picked up on its own.
        await Fixture.DrainAsync();
        Assert.Equal(CarteiroFixture.MaxAttempts, Fixture.Receiver.Received.Count);

        var deadList = await Client.GetOkAsync<List<DeliveryResponse>>("/deliveries?status=Dead");
        Assert.Equal(deliveryId, Assert.Single(deadList).Id);

        // The receiver is fixed; an operator re-drives the delivery.
        Fixture.Receiver.RespondWith(HttpStatusCode.OK);
        var retry = await Client.PostAsync($"/deliveries/{deliveryId}/retry", null);
        await retry.EnsureStatusAsync(HttpStatusCode.OK);
        Assert.Equal(DeliveryStatus.Pending, (await retry.ReadAsync<DeliveryResponse>()).Status);

        await Fixture.DrainAsync();

        var recovered = await Client.GetOkAsync<DeliveryDetailResponse>($"/deliveries/{deliveryId}");
        Assert.Equal(DeliveryStatus.Succeeded, recovered.Status);
        Assert.Equal(4, recovered.AttemptCount);
        Assert.True(recovered.Attempts[^1].Succeeded);
    }

    [Fact]
    public async Task Only_dead_deliveries_can_be_retried_manually()
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        var @event = await Client.PublishOkAsync("order.created", "{}");

        var response = await Client.PostAsync($"/deliveries/{@event.Deliveries[0].Id}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/deliveries/{Guid.NewGuid()}/retry", null)).StatusCode);
    }

    [Fact]
    public async Task An_endpoint_is_disabled_after_too_many_consecutive_failures()
    {
        var endpoint = await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        Fixture.Receiver.RespondWith(HttpStatusCode.InternalServerError);

        // Each event fails once: the streak counts attempts across deliveries.
        for (var i = 0; i < CarteiroFixture.EndpointFailureThreshold; i++)
        {
            await Client.PublishOkAsync("order.created", $$"""{"n":{{i}}}""");
        }

        await Fixture.DrainAsync();

        var disabled = await Client.GetOkAsync<EndpointResponse>($"/endpoints/{endpoint.Id}");
        Assert.False(disabled.Enabled);
        Assert.Equal(CarteiroFixture.EndpointFailureThreshold, disabled.ConsecutiveFailures);
        Assert.Contains("consecutive failed attempts", disabled.DisabledReason);

        // Pending retries of a disabled endpoint are parked, not attempted.
        Fixture.Time.Advance(CarteiroFixture.MaxDelay);
        await Fixture.DrainAsync();
        Assert.Equal(CarteiroFixture.EndpointFailureThreshold, Fixture.Receiver.Received.Count);

        // New events are not fanned out to it.
        var ignored = await Client.PublishOkAsync("order.created", "{}");
        Assert.Empty(ignored.Deliveries);

        // Re-enabling resets the streak and the parked deliveries resume.
        Fixture.Receiver.RespondWith(HttpStatusCode.OK);
        var enable = await Client.PatchAsJsonAsync($"/endpoints/{endpoint.Id}", new { enabled = true });
        var enabled = await enable.ReadAsync<EndpointResponse>();
        Assert.True(enabled.Enabled);
        Assert.Equal(0, enabled.ConsecutiveFailures);

        await Fixture.DrainAsync();
        var succeeded = await Client.GetOkAsync<List<DeliveryResponse>>($"/deliveries?endpointId={endpoint.Id}&status=Succeeded");
        Assert.Equal(CarteiroFixture.EndpointFailureThreshold, succeeded.Count);
    }

    [Fact]
    public async Task A_success_resets_the_failure_streak()
    {
        var endpoint = await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        Fixture.Receiver.RespondWith(HttpStatusCode.InternalServerError);
        await Client.PublishOkAsync("order.created", """{"n":1}""");
        await Client.PublishOkAsync("order.created", """{"n":2}""");
        await Fixture.DrainAsync();
        Assert.Equal(2, (await Client.GetOkAsync<EndpointResponse>($"/endpoints/{endpoint.Id}")).ConsecutiveFailures);

        Fixture.Receiver.RespondWith(HttpStatusCode.OK);
        await Client.PublishOkAsync("order.created", """{"n":3}""");
        await Fixture.DrainAsync();

        Assert.Equal(0, (await Client.GetOkAsync<EndpointResponse>($"/endpoints/{endpoint.Id}")).ConsecutiveFailures);
    }

    [Fact]
    public async Task A_delivery_whose_worker_died_is_picked_up_again_after_the_lease_expires()
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        await Client.PublishOkAsync("order.created", "{}");

        // Worker A claims the delivery and then "crashes": it never records a result.
        var crashed = await Fixture.NewProcessor().ClaimAsync(CancellationToken.None);
        Assert.Single(crashed);

        // While the lease holds, nobody else may take it.
        Assert.Empty(await Fixture.NewProcessor().ClaimAsync(CancellationToken.None));

        Fixture.Time.Advance(CarteiroFixture.ClaimLease + TimeSpan.FromSeconds(1));
        await Fixture.DrainAsync();

        Assert.Single(Fixture.Receiver.Received);
        await using var db = Fixture.CreateDbContext();
        Assert.Equal(DeliveryStatus.Succeeded, (await db.Deliveries.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_worker_that_lost_its_lease_cannot_overwrite_the_result()
    {
        await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created");
        await Client.PublishOkAsync("order.created", "{}");

        // Worker A claims, then stalls for longer than the lease.
        var slow = Fixture.NewProcessor();
        var slowClaim = Assert.Single(await slow.ClaimAsync(CancellationToken.None));
        Fixture.Time.Advance(CarteiroFixture.ClaimLease + TimeSpan.FromSeconds(1));

        // Worker B takes over and delivers successfully.
        await Fixture.DrainAsync();

        // Worker A wakes up with a stale failure. It must be discarded.
        var recorded = await slow.CompleteAsync(
            slowClaim, AttemptOutcome.Failure(500, TimeSpan.FromSeconds(1), "late"), CancellationToken.None);

        Assert.False(recorded);
        await using var db = Fixture.CreateDbContext();
        var delivery = await db.Deliveries.Include(d => d.Attempts).SingleAsync();
        Assert.Equal(DeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Single(delivery.Attempts);
    }

    [Fact]
    public async Task Deliveries_can_be_filtered_and_paged()
    {
        var a = await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        await Client.CreateEndpointAsync("https://b.example/hooks", "order.created");
        for (var i = 0; i < 3; i++)
        {
            await Client.PublishOkAsync("order.created", $$"""{"n":{{i}}}""");
            Fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var all = await Client.GetOkAsync<List<DeliveryResponse>>("/deliveries");
        var forA = await Client.GetOkAsync<List<DeliveryResponse>>($"/deliveries?endpointId={a.Id}");
        var page = await Client.GetOkAsync<List<DeliveryResponse>>($"/deliveries?endpointId={a.Id}&limit=2&offset=2");

        Assert.Equal(6, all.Count);
        Assert.Equal(3, forA.Count);
        Assert.Equal(forA[2].Id, Assert.Single(page).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/deliveries?limit=0")).StatusCode);
    }
}
