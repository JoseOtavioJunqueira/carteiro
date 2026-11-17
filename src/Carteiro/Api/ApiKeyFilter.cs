using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Carteiro.Api;

/// <summary>
/// Rejects requests that do not carry a configured API key in the X-Api-Key header.
/// </summary>
internal sealed class ApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    private readonly byte[][] _keyHashes;

    public ApiKeyFilter(IOptions<CarteiroOptions> options)
    {
        _keyHashes = options.Value.ApiKeys.Select(Hash).ToArray();
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();

        if (provided.Length == 0 || !IsKnownKey(provided))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: $"A valid API key is required in the {HeaderName} header.");
        }

        return await next(context);
    }

    private bool IsKnownKey(string provided)
    {
        var providedHash = Hash(provided);
        var known = false;

        // Compare against every key without short-circuiting, and compare hashes rather than the
        // keys themselves so the comparison time does not depend on key length or content.
        foreach (var keyHash in _keyHashes)
        {
            known |= CryptographicOperations.FixedTimeEquals(providedHash, keyHash);
        }

        return known;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
