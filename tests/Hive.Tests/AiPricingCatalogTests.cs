using Hive.Domain.Ai;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiPricingCatalogTests
{
    private static readonly AiProviderMetadata CanonicalProvider =
        new("openai", "gpt-5-mini");

    [Fact]
    public void Calculates_decimal_cost_and_records_applied_versioned_price()
    {
        var catalog = Catalog();

        var calculated = catalog.TryCalculate(
            CanonicalProvider,
            new AiTokenUsage(1_500_000, 250_000, 1_750_000),
            out var cost,
            out var appliedPricing);

        Assert.True(calculated);
        Assert.NotNull(cost);
        Assert.Equal(0.875m, cost.Amount);
        Assert.Equal("USD", cost.Currency);
        Assert.True(cost.IsEstimated);
        Assert.NotNull(appliedPricing);
        Assert.Equal("openai-2026-07-13", appliedPricing.Version);
        Assert.Equal(1_000_000, appliedPricing.TokenUnit);
        Assert.Equal(0.25m, appliedPricing.InputPrice);
        Assert.Equal(2m, appliedPricing.OutputPrice);
    }

    [Fact]
    public void Resolves_exact_model_alias_without_changing_canonical_price()
    {
        var calculated = Catalog().TryCalculate(
            new AiProviderMetadata("openai", "gpt-5-mini-2025-08-07"),
            new AiTokenUsage(1_000, 500, 1_500),
            out var cost,
            out var appliedPricing);

        Assert.True(calculated);
        Assert.Equal(0.00125m, cost?.Amount);
        Assert.Equal("openai-2026-07-13", appliedPricing?.Version);
    }

    [Fact]
    public void Missing_complete_usage_or_model_price_keeps_cost_unavailable()
    {
        var catalog = Catalog();

        Assert.False(catalog.TryCalculate(
            CanonicalProvider,
            new AiTokenUsage(outputTokens: 10),
            out var incompleteCost,
            out var incompletePricing));
        Assert.Null(incompleteCost);
        Assert.Null(incompletePricing);

        Assert.False(catalog.TryCalculate(
            new AiProviderMetadata("openai", "unpriced-model"),
            new AiTokenUsage(10, 5, 15),
            out var unpricedCost,
            out var unpricedPricing));
        Assert.Null(unpricedCost);
        Assert.Null(unpricedPricing);
    }

    [Fact]
    public void Real_zero_usage_can_produce_zero_estimated_cost()
    {
        var calculated = Catalog().TryCalculate(
            CanonicalProvider,
            new AiTokenUsage(0, 0, 0),
            out var cost,
            out _);

        Assert.True(calculated);
        Assert.Equal(0m, cost?.Amount);
    }

    private static AiPricingCatalog Catalog() =>
        new(
            "openai-2026-07-13",
            1_000_000,
            [
                new AiModelPricing(
                    "openai",
                    "gpt-5-mini",
                    ["gpt-5-mini-2025-08-07"],
                    0.25m,
                    2m,
                    "USD"),
            ]);
}
