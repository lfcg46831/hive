using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

public sealed record AuditExportCost
{
    public AuditExportCost(
        decimal amount,
        string currency,
        bool estimated,
        string? pricingVersion = null,
        int? pricingTokenUnit = null,
        decimal? inputPricePerTokenUnit = null,
        decimal? outputPricePerTokenUnit = null)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var guardedCurrency = AuditExportContractGuards.Text(
            currency,
            nameof(currency),
            maxLength: 3);
        if (guardedCurrency.Length != 3 ||
            !guardedCurrency.All(character => character is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException(
                "Currency must be a three-letter uppercase code.",
                nameof(currency));
        }

        var pricingFields = new object?[]
        {
            pricingVersion,
            pricingTokenUnit,
            inputPricePerTokenUnit,
            outputPricePerTokenUnit,
        };
        if (pricingFields.Any(field => field is not null) &&
            pricingFields.Any(field => field is null))
        {
            throw new ArgumentException(
                "Pricing metadata must be either complete or absent.",
                nameof(pricingVersion));
        }

        if (pricingTokenUnit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pricingTokenUnit));
        }

        if (inputPricePerTokenUnit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputPricePerTokenUnit));
        }

        if (outputPricePerTokenUnit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputPricePerTokenUnit));
        }

        Amount = amount;
        Currency = guardedCurrency;
        Estimated = estimated;
        PricingVersion = AuditExportContractGuards.OptionalText(
            pricingVersion,
            nameof(pricingVersion));
        PricingTokenUnit = pricingTokenUnit;
        InputPricePerTokenUnit = inputPricePerTokenUnit;
        OutputPricePerTokenUnit = outputPricePerTokenUnit;
    }

    [JsonPropertyName("amount")]
    public decimal Amount { get; }

    [JsonPropertyName("currency")]
    public string Currency { get; }

    [JsonPropertyName("estimated")]
    public bool Estimated { get; }

    [JsonPropertyName("pricing_version")]
    public string? PricingVersion { get; }

    [JsonPropertyName("pricing_token_unit")]
    public int? PricingTokenUnit { get; }

    [JsonPropertyName("input_price_per_token_unit")]
    public decimal? InputPricePerTokenUnit { get; }

    [JsonPropertyName("output_price_per_token_unit")]
    public decimal? OutputPricePerTokenUnit { get; }
}
