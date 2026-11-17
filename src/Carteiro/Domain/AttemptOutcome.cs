namespace Carteiro.Domain;

/// <summary>
/// The observable result of one HTTP request to a receiver.
/// </summary>
public sealed record AttemptOutcome(bool Succeeded, int? StatusCode, TimeSpan Duration, string? Error)
{
    public static AttemptOutcome Success(int statusCode, TimeSpan duration) =>
        new(true, statusCode, duration, null);

    public static AttemptOutcome Failure(int? statusCode, TimeSpan duration, string error) =>
        new(false, statusCode, duration, error);
}
