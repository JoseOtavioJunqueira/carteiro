using Carteiro.Dispatch;

namespace Carteiro.UnitTests;

public class WebhookSignerTests
{
    private const string Secret = "whsec_test_secret";
    private const string Payload = """{"hello":"world"}""";
    private const long Timestamp = 1_700_000_000;

    private static readonly DateTimeOffset SentAt = DateTimeOffset.FromUnixTimeSeconds(Timestamp);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void Signature_matches_a_vector_computed_with_openssl()
    {
        // printf '%s' '1700000000.{"hello":"world"}' | openssl dgst -sha256 -hmac 'whsec_test_secret'
        const string expected = "v1=86748dbec9cc87a9219f8a96632da703271bf5e85aa0afa2b310c37ba059d514";

        Assert.Equal(expected, WebhookSigner.Sign(Secret, Timestamp, Payload));
    }

    [Fact]
    public void A_signature_verifies_against_the_same_inputs()
    {
        var signature = WebhookSigner.Sign(Secret, Timestamp, Payload);

        Assert.True(WebhookSigner.Verify(Secret, Timestamp, Payload, signature, SentAt.AddSeconds(3), Tolerance));
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        var signature = WebhookSigner.Sign(Secret, Timestamp, Payload);

        Assert.False(WebhookSigner.Verify(Secret, Timestamp, """{"hello":"mallory"}""", signature, SentAt, Tolerance));
    }

    [Fact]
    public void A_different_secret_is_rejected()
    {
        var signature = WebhookSigner.Sign("another-secret", Timestamp, Payload);

        Assert.False(WebhookSigner.Verify(Secret, Timestamp, Payload, signature, SentAt, Tolerance));
    }

    [Fact]
    public void A_replayed_request_outside_the_tolerance_window_is_rejected()
    {
        var signature = WebhookSigner.Sign(Secret, Timestamp, Payload);

        Assert.False(WebhookSigner.Verify(Secret, Timestamp, Payload, signature, SentAt.AddMinutes(6), Tolerance));
    }

    [Fact]
    public void Rewriting_the_timestamp_to_dodge_the_window_breaks_the_signature()
    {
        var signature = WebhookSigner.Sign(Secret, Timestamp, Payload);
        var later = SentAt.AddHours(1);

        Assert.False(WebhookSigner.Verify(Secret, later.ToUnixTimeSeconds(), Payload, signature, later, Tolerance));
    }

    [Fact]
    public void A_timestamp_from_the_future_beyond_the_tolerance_is_rejected()
    {
        var signature = WebhookSigner.Sign(Secret, Timestamp, Payload);

        Assert.False(WebhookSigner.Verify(Secret, Timestamp, Payload, signature, SentAt.AddMinutes(-6), Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("86748dbec9cc87a9219f8a96632da703271bf5e85aa0afa2b310c37ba059d514")] // missing scheme prefix
    [InlineData("v2=86748dbec9cc87a9219f8a96632da703271bf5e85aa0afa2b310c37ba059d514")]
    [InlineData("v1=not-hex")]
    [InlineData("v1=8674")]
    public void Malformed_signature_headers_are_rejected(string? header)
    {
        Assert.False(WebhookSigner.Verify(Secret, Timestamp, Payload, header, SentAt, Tolerance));
    }

    [Fact]
    public void Non_ascii_payloads_are_signed_as_utf8()
    {
        const string payload = """{"cidade":"São Paulo"}""";
        var signature = WebhookSigner.Sign(Secret, Timestamp, payload);

        Assert.True(WebhookSigner.Verify(Secret, Timestamp, payload, signature, SentAt, Tolerance));
        Assert.NotEqual(signature, WebhookSigner.Sign(Secret, Timestamp, """{"cidade":"Sao Paulo"}"""));
    }
}
