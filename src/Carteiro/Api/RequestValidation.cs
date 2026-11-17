using System.Text.RegularExpressions;
using Carteiro.Dispatch;
using Carteiro.Domain;

namespace Carteiro.Api;

/// <summary>
/// Collects validation errors per field, in the shape <c>Results.ValidationProblem</c> expects.
/// </summary>
internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public IResult ToProblem() =>
        Results.ValidationProblem(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}

internal static partial class RequestValidation
{
    public const int MaxUrlLength = 2048;
    public const int MaxDescriptionLength = 500;
    public const int MinSecretLength = 16;
    public const int MaxSecretLength = 255;
    public const int MaxIdempotencyKeyLength = 255;
    public const int MaxPayloadBytes = 256 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,99}$")]
    private static partial Regex EventTypePattern();

    public static bool IsValidEventType(string? value) =>
        value is not null && EventTypePattern().IsMatch(value);

    public static void ValidateUrl(string? url, bool allowPrivateNetworks, ValidationErrors errors)
    {
        const string field = "url";

        if (string.IsNullOrWhiteSpace(url))
        {
            errors.Add(field, "The url is required.");
            return;
        }

        if (url.Length > MaxUrlLength)
        {
            errors.Add(field, $"The url must be at most {MaxUrlLength} characters.");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(field, "The url must be an absolute http or https URL.");
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add(field, "The url must not contain credentials.");
        }

        if (!allowPrivateNetworks && SsrfGuard.IsObviouslyInternal(uri))
        {
            errors.Add(field, "The url points to a loopback or private address, which is not allowed.");
        }
    }

    public static void ValidateEventTypes(IReadOnlyCollection<string>? eventTypes, ValidationErrors errors)
    {
        const string field = "eventTypes";

        if (eventTypes is null || eventTypes.Count == 0)
        {
            errors.Add(field, $"At least one event type is required. Use \"{WebhookEndpoint.AllEventTypes}\" to subscribe to everything.");
            return;
        }

        foreach (var eventType in eventTypes)
        {
            if (eventType != WebhookEndpoint.AllEventTypes && !IsValidEventType(eventType))
            {
                errors.Add(field, $"'{eventType}' is not a valid event type. Use letters, digits, '.', '_', ':' or '-' (max 100 characters).");
            }
        }
    }

    public static void ValidateDescription(string? description, ValidationErrors errors)
    {
        if (description is { Length: > MaxDescriptionLength })
        {
            errors.Add("description", $"The description must be at most {MaxDescriptionLength} characters.");
        }
    }

    public static void ValidateSecret(string? secret, ValidationErrors errors)
    {
        if (secret is not null && secret.Length is < MinSecretLength or > MaxSecretLength)
        {
            errors.Add("secret", $"The secret must be between {MinSecretLength} and {MaxSecretLength} characters. Omit it to have one generated.");
        }
    }
}
