using Carteiro.Domain;

namespace Carteiro.UnitTests;

public class WebhookEndpointTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static WebhookEndpoint NewEndpoint(params string[] eventTypes) =>
        WebhookEndpoint.Create("https://example.com/hooks", "secret", eventTypes, null, T0);

    [Fact]
    public void A_new_endpoint_is_enabled()
    {
        var endpoint = NewEndpoint("order.created");

        Assert.True(endpoint.Enabled);
        Assert.Equal(0, endpoint.ConsecutiveFailures);
        Assert.Null(endpoint.DisabledAt);
    }

    [Fact]
    public void Event_types_are_trimmed_deduplicated_and_sorted()
    {
        var endpoint = NewEndpoint(" order.paid ", "order.created", "order.paid");

        Assert.Equal(["order.created", "order.paid"], endpoint.EventTypes);
    }

    [Fact]
    public void An_endpoint_needs_at_least_one_event_type()
    {
        Assert.Throws<ArgumentException>(() => NewEndpoint());
    }

    [Theory]
    [InlineData("order.created", true)]
    [InlineData("order.paid", false)]
    [InlineData("order", false)]
    public void Subscription_is_an_exact_match_on_the_event_type(string eventType, bool expected)
    {
        var endpoint = NewEndpoint("order.created");

        Assert.Equal(expected, endpoint.IsSubscribedTo(eventType));
    }

    [Fact]
    public void The_wildcard_subscribes_to_everything()
    {
        var endpoint = NewEndpoint(WebhookEndpoint.AllEventTypes);

        Assert.True(endpoint.IsSubscribedTo("anything.at.all"));
    }

    [Fact]
    public void Disabling_records_when_and_why()
    {
        var endpoint = NewEndpoint("order.created");
        var now = T0.AddHours(1);

        endpoint.Disable("Disabled by API request", now);

        Assert.False(endpoint.Enabled);
        Assert.Equal(now, endpoint.DisabledAt);
        Assert.Equal("Disabled by API request", endpoint.DisabledReason);
    }

    [Fact]
    public void Enabling_clears_the_disabled_state()
    {
        var endpoint = NewEndpoint("order.created");
        endpoint.Disable("because", T0.AddHours(1));

        endpoint.Enable(T0.AddHours(2));

        Assert.True(endpoint.Enabled);
        Assert.Null(endpoint.DisabledAt);
        Assert.Null(endpoint.DisabledReason);
        Assert.Equal(0, endpoint.ConsecutiveFailures);
        Assert.Equal(T0.AddHours(2), endpoint.UpdatedAt);
    }
}
