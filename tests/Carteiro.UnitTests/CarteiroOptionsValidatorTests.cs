namespace Carteiro.UnitTests;

public class CarteiroOptionsValidatorTests
{
    private static readonly CarteiroOptionsValidator Validator = new();

    private static CarteiroOptions ValidOptions() => new() { ApiKeys = ["a-key"] };

    [Fact]
    public void Defaults_plus_an_api_key_are_valid()
    {
        Assert.True(Validator.Validate(null, ValidOptions()).Succeeded);
    }

    [Fact]
    public void At_least_one_api_key_is_required()
    {
        var result = Validator.Validate(null, new CarteiroOptions());

        Assert.True(result.Failed);
        Assert.Contains("ApiKeys", result.FailureMessage);
    }

    [Fact]
    public void Blank_api_keys_are_rejected()
    {
        var options = ValidOptions();
        options.ApiKeys.Add(" ");

        Assert.True(Validator.Validate(null, options).Failed);
    }

    [Fact]
    public void The_claim_lease_must_outlast_the_request_timeout()
    {
        var options = ValidOptions();
        options.Delivery.RequestTimeout = TimeSpan.FromSeconds(30);
        options.Worker.ClaimLease = TimeSpan.FromSeconds(30);

        var result = Validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ClaimLease", result.FailureMessage);
    }

    [Fact]
    public void Max_delay_cannot_be_below_base_delay()
    {
        var options = ValidOptions();
        options.Delivery.BaseDelay = TimeSpan.FromMinutes(5);
        options.Delivery.MaxDelay = TimeSpan.FromMinutes(1);

        Assert.True(Validator.Validate(null, options).Failed);
    }

    [Fact]
    public void All_problems_are_reported_at_once()
    {
        var options = new CarteiroOptions();
        options.Delivery.MaxAttempts = 0;
        options.Worker.BatchSize = 0;

        var result = Validator.Validate(null, options);

        Assert.Equal(3, result.Failures!.Count());
    }
}
