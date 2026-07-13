namespace Hive.Infrastructure.Ai;

public sealed class AiPricingCatalogOptions
{
    public string? Version { get; set; }

    public int? TokenUnit { get; set; }

    public AiModelPricingOptions[]? Models { get; set; }
}

public sealed class AiModelPricingOptions
{
    public string? ProviderId { get; set; }

    public string? ModelId { get; set; }

    public string[]? Aliases { get; set; }

    public decimal? InputPrice { get; set; }

    public decimal? OutputPrice { get; set; }

    public string? Currency { get; set; }
}
