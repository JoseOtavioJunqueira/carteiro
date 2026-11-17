using System.Collections.Concurrent;
using System.Net;
using Carteiro.Dispatch;
using Carteiro.Domain;
using Carteiro.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Carteiro.IntegrationTests;

public class ConcurrentWorkerTests(CarteiroFixture fixture) : IntegrationTest(fixture)
{
    private const int EventCount = 150;

    [Fact]
    public async Task Concurrent_workers_never_deliver_the_same_delivery_twice()
    {
        await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        await Client.CreateEndpointAsync("https://b.example/hooks", "order.created");
        for (var i = 0; i < EventCount; i++)
        {
            await Client.PublishOkAsync("order.created", $$"""{"n":{{i}}}""");
        }

        const int expectedDeliveries = EventCount * 2;

        // A small random delay makes the workers' claim and complete phases interleave.
        Fixture.Receiver.RespondWith(async _ =>
        {
            await Task.Delay(Random.Shared.Next(0, 5));
            return HttpStatusCode.OK;
        });

        // Four independent processors, each with its own DbContexts and connections, racing on
        // the same table, the way four worker processes would.
        var workers = Enumerable.Range(0, 4).Select(_ => Fixture.NewProcessor()).ToList();
        var claimedBy = new ConcurrentDictionary<int, int>();

        await Task.WhenAll(workers.Select((worker, index) => Task.Run(async () =>
        {
            int claimed;
            while ((claimed = await worker.RunOnceAsync(CancellationToken.None)) > 0)
            {
                claimedBy.AddOrUpdate(index, claimed, (_, total) => total + claimed);
            }
        })));

        var sentDeliveryIds = Fixture.Receiver.Received.Select(r => r.Header(WebhookHeaders.DeliveryId)).ToList();

        Assert.Equal(expectedDeliveries, sentDeliveryIds.Count);
        Assert.Equal(expectedDeliveries, sentDeliveryIds.Distinct().Count());

        // The work really was shared, otherwise this test would prove nothing.
        Assert.True(claimedBy.Count > 1, $"Only {claimedBy.Count} worker(s) claimed anything.");

        await using var db = Fixture.CreateDbContext();
        Assert.Equal(expectedDeliveries, await db.Deliveries.CountAsync(d => d.Status == DeliveryStatus.Succeeded));
        Assert.Equal(expectedDeliveries, await db.DeliveryAttempts.CountAsync());
        Assert.False(await db.Deliveries.AnyAsync(d => d.AttemptCount != 1));
    }

    [Fact]
    public async Task Simultaneous_claims_return_disjoint_batches()
    {
        await Client.CreateEndpointAsync("https://a.example/hooks", "order.created");
        for (var i = 0; i < 40; i++)
        {
            await Client.PublishOkAsync("order.created", $$"""{"n":{{i}}}""");
        }

        // Claim only, never complete: every claimed row stays leased for the rest of the test,
        // so any overlap between the batches would be a double claim.
        using var start = new ManualResetEventSlim();
        var claims = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var processor = Fixture.NewProcessor();
            start.Wait();
            return await processor.ClaimAsync(CancellationToken.None);
        })).ToList();

        start.Set();
        var batches = await Task.WhenAll(claims);
        var claimedIds = batches.SelectMany(b => b.Select(c => c.DeliveryId)).ToList();

        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());
        Assert.Equal(40, claimedIds.Count); // 8 workers x batch size 10 > 40 rows: all claimed exactly once
    }
}
