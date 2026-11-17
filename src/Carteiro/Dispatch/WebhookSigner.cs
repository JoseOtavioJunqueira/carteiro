using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Carteiro.Dispatch;

/// <summary>
/// Signs and verifies webhook payloads.
/// </summary>
/// <remarks>
/// The signed content is <c>{timestamp}.{payload}</c>, where timestamp is Unix time in seconds
/// and payload is the exact request body. The signature is HMAC-SHA256 with the endpoint secret,
/// hex encoded and prefixed with the scheme version: <c>v1=4f2a...</c>.
///
/// Including the timestamp in the signed content means an attacker who captures a request
/// cannot replay it later with a fresh timestamp: changing the timestamp invalidates the signature,
/// and keeping it makes the request fall outside the receiver's tolerance window.
/// </remarks>
public static class WebhookSigner
{
    public const string SchemePrefix = "v1=";

    public static string Sign(string secret, long unixTimestampSeconds, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(payload);

        return SchemePrefix + Convert.ToHexStringLower(ComputeMac(secret, unixTimestampSeconds, payload));
    }

    /// <summary>
    /// Reference implementation of what a receiver should do. Carteiro itself never calls this
    /// outside of tests; it lives here so the contract is executable and covered.
    /// </summary>
    public static bool Verify(
        string secret,
        long unixTimestampSeconds,
        string payload,
        string? signatureHeader,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(payload);

        if (signatureHeader is null || !signatureHeader.StartsWith(SchemePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(unixTimestampSeconds);
        if (age.Duration() > tolerance)
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(signatureHeader.AsSpan(SchemePrefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeMac(secret, unixTimestampSeconds, payload);

        // Constant-time comparison: a plain == would leak how many leading bytes matched.
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static byte[] ComputeMac(string secret, long unixTimestampSeconds, string payload)
    {
        var signedContent = unixTimestampSeconds.ToString(CultureInfo.InvariantCulture) + "." + payload;
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signedContent));
    }
}
