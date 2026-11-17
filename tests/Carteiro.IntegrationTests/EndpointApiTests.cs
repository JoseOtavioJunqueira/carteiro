using System.Net;
using System.Net.Http.Json;
using Carteiro.Api;
using Carteiro.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Carteiro.IntegrationTests;

public class EndpointApiTests(CarteiroFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Requests_without_an_api_key_are_rejected()
    {
        using var anonymous = Fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync("/endpoints");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requests_with_a_wrong_api_key_are_rejected()
    {
        using var client = Fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyFilter.HeaderName, "not-the-key");

        var response = await client.GetAsync("/endpoints");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creating_an_endpoint_returns_a_generated_secret_once()
    {
        var created = await Client.CreateEndpointAsync("https://receiver.example/hooks", "order.created", "order.paid");

        Assert.StartsWith("whsec_", created.Secret);
        Assert.True(created.Enabled);
        Assert.Equal(["order.created", "order.paid"], created.EventTypes);

        var fetched = await Client.GetStringAsync($"/endpoints/{created.Id}");
        Assert.DoesNotContain(created.Secret, fetched);
        Assert.DoesNotContain("secret", fetched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_caller_supplied_secret_is_used()
    {
        var response = await Client.PostAsJsonAsync(
            "/endpoints",
            new CreateEndpointRequest("https://receiver.example/hooks", ["order.created"], null, "my-own-secret-of-decent-length"));

        var created = await response.ReadAsync<EndpointCreatedResponse>();
        Assert.Equal("my-own-secret-of-decent-length", created.Secret);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://receiver.example/hooks")]
    [InlineData("/relative/path")]
    [InlineData("https://user:pass@receiver.example/hooks")]
    public async Task Invalid_urls_are_rejected(string url)
    {
        var response = await Client.PostAsJsonAsync("/endpoints", new CreateEndpointRequest(url, ["order.created"], null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<ValidationProblemDetails>();
        Assert.Contains("url", problem.Errors.Keys);
    }

    [Fact]
    public async Task Event_types_are_validated()
    {
        var response = await Client.PostAsJsonAsync(
            "/endpoints",
            new CreateEndpointRequest("https://receiver.example/hooks", ["order created!"], null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Private_destinations_are_rejected_when_private_networks_are_not_allowed()
    {
        using var strictFactory = Fixture.CreateFactory(("Carteiro:Delivery:AllowPrivateNetworks", "false"));
        using var client = Fixture.CreateClient(strictFactory);

        foreach (var url in new[] { "http://localhost:9000/hooks", "http://127.0.0.1/hooks", "http://169.254.169.254/latest", "http://10.1.2.3/" })
        {
            var response = await client.PostAsJsonAsync("/endpoints", new CreateEndpointRequest(url, ["order.created"], null, null));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.ReadAsync<ValidationProblemDetails>();
            Assert.Contains("private", problem.Errors["url"].Single());
        }
    }

    [Fact]
    public async Task Endpoints_can_be_listed_updated_and_deleted()
    {
        var first = await Client.CreateEndpointAsync("https://one.example/hooks");
        Fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var second = await Client.CreateEndpointAsync("https://two.example/hooks");

        var list = await Client.GetOkAsync<List<EndpointResponse>>("/endpoints");
        Assert.Equal([second.Id, first.Id], list.Select(e => e.Id));

        var patch = await Client.PatchAsJsonAsync(
            $"/endpoints/{first.Id}",
            new UpdateEndpointRequest(null, ["*"], "everything", Enabled: false));
        var updated = await patch.ReadAsync<EndpointResponse>();
        Assert.Equal(["*"], updated.EventTypes);
        Assert.Equal("everything", updated.Description);
        Assert.False(updated.Enabled);
        Assert.Equal("https://one.example/hooks", updated.Url);

        var delete = await Client.DeleteAsync($"/endpoints/{first.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/endpoints/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/endpoints/{first.Id}")).StatusCode);
    }

    [Fact]
    public async Task Health_checks_and_openapi_document_are_public()
    {
        using var anonymous = Fixture.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready")).StatusCode);

        var openApi = await anonymous.GetStringAsync("/openapi/v1.json");
        Assert.Contains("\"/events\"", openApi);
        Assert.Contains("/deliveries/{id}/retry", openApi);
        Assert.Contains("X-Api-Key", openApi);
    }
}
