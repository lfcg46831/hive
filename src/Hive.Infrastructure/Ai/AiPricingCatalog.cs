using System.Collections.Immutable;
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

public sealed class AiPricingCatalog
{
    private readonly ImmutableDictionary<PricingKey, AiModelPricing> _models;

    public AiPricingCatalog(
        string version,
        int tokenUnit,
        IEnumerable<AiModelPricing> models)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            !string.Equals(version, version.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Pricing catalog version must be canonical text.",
                nameof(version));
        }

        if (tokenUnit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenUnit),
                tokenUnit,
                "Pricing catalog token unit must be greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(models);
        var snapshot = models.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(model => model is null))
        {
            throw new ArgumentException(
                "Pricing catalog requires at least one model price.",
                nameof(models));
        }

        var builder = ImmutableDictionary.CreateBuilder<PricingKey, AiModelPricing>();
        foreach (var model in snapshot)
        {
            AddModelKey(builder, model.ProviderId, model.ModelId, model);
            foreach (var alias in model.Aliases)
            {
                AddModelKey(builder, model.ProviderId, alias, model);
            }
        }

        Version = version;
        TokenUnit = tokenUnit;
        Models = snapshot.ToImmutableArray();
        _models = builder.ToImmutable();
    }

    public string Version { get; }

    public int TokenUnit { get; }

    public IReadOnlyList<AiModelPricing> Models { get; }

    public bool TryCalculate(
        AiProviderMetadata provider,
        AiTokenUsage? usage,
        out AiCostMetadata? cost,
        out AiAppliedPricing? appliedPricing)
    {
        ArgumentNullException.ThrowIfNull(provider);

        cost = null;
        appliedPricing = null;

        if (usage?.InputTokens is not { } inputTokens ||
            usage.OutputTokens is not { } outputTokens ||
            !_models.TryGetValue(
                new PricingKey(provider.ProviderId, provider.ModelId),
                out var model))
        {
            return false;
        }

        decimal amount;
        try
        {
            amount =
                (inputTokens / (decimal)TokenUnit * model.InputPrice) +
                (outputTokens / (decimal)TokenUnit * model.OutputPrice);
        }
        catch (OverflowException)
        {
            return false;
        }

        cost = new AiCostMetadata(amount, model.Currency, isEstimated: true);
        appliedPricing = new AiAppliedPricing(
            Version,
            TokenUnit,
            model.InputPrice,
            model.OutputPrice,
            model.Currency);
        return true;
    }

    private static void AddModelKey(
        IDictionary<PricingKey, AiModelPricing> models,
        string providerId,
        string modelId,
        AiModelPricing model)
    {
        if (!models.TryAdd(new PricingKey(providerId, modelId), model))
        {
            throw new ArgumentException(
                $"Pricing catalog provider/model key '{providerId}/{modelId}' is ambiguous.",
                nameof(models));
        }
    }

    private readonly record struct PricingKey(string ProviderId, string ModelId);
}

public sealed class AiModelPricing
{
    public AiModelPricing(
        string providerId,
        string modelId,
        IEnumerable<string>? aliases,
        decimal inputPrice,
        decimal outputPrice,
        string currency)
    {
        if (inputPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputPrice));
        }

        if (outputPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputPrice));
        }

        ProviderId = RequireText(providerId, nameof(providerId));
        ModelId = RequireText(modelId, nameof(modelId));
        Aliases = (aliases ?? [])
            .Select(alias => RequireText(alias, nameof(aliases)))
            .ToImmutableArray();
        InputPrice = inputPrice;
        OutputPrice = outputPrice;
        Currency = RequireText(currency, nameof(currency));
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public IReadOnlyList<string> Aliases { get; }

    public decimal InputPrice { get; }

    public decimal OutputPrice { get; }

    public string Currency { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Pricing values must be canonical text.",
                parameterName);
        }

        return value;
    }
}
