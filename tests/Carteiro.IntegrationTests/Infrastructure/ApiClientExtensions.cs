using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Carteiro.Api;

namespace Carteiro.IntegrationTests.Infrastructure;

internal static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<EndpointCreatedResponse> CreateEndpointAsync(
        this HttpClient client,
        string url = "https://receiver.example/hooks",
        params string[] eventTypes)
    {
        var request = new CreateEndpointRequest(url, eventTypes.Length == 0 ? ["order.created"] : [.. eventTypes], null, null);
        var response = await client.PostAsJsonAsync("/endpoints", request, Json);
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<EndpointCreatedResponse>(Json))!;
    }

    public static Task<HttpResponseMessage> PublishAsync(
        this HttpClient client,
        string type,
        string payloadJson,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/events")
        {
            Content = new StringContent($$"""{"type":"{{type}}","payload":{{payloadJson}}}""", System.Text.Encoding.UTF8, "application/json"),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static async Task<EventResponse> PublishOkAsync(this HttpClient client, string type, string payloadJson, string? idempotencyKey = null)
    {
        var response = await client.PublishAsync(type, payloadJson, idempotencyKey);
        await response.EnsureStatusAsync(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<EventResponse>(Json))!;
    }

    public static async Task<T> GetOkAsync<T>(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        await response.EnsureStatusAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>Like EnsureSuccessStatusCode, but checks the exact code and shows the body on failure.</summary>
    public static async Task EnsureStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected HTTP {(int)expected} but got {(int)response.StatusCode}: {body}");
        }
    }
}
