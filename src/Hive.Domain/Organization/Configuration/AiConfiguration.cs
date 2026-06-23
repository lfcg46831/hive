namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The AI runtime configuration of an <see cref="OccupantType.AiAgent"/> (§6.2 <c>occupant.ai</c>):
/// the <see cref="Provider"/> registered in the AI gateway, the <see cref="Model"/>, optional
/// sampling parameters, the <see cref="Processing"/> mode and <see cref="BatchWindow"/>, the ordered
/// <see cref="Fallback"/> chain and the optional <see cref="Budget"/>. Values are captured as
/// declared; provider/model resolution and budget coherence are not checked here.
/// </summary>
public sealed record AiConfiguration
{
    /// <summary>Creates the AI configuration for <paramref name="provider"/>/<paramref name="model"/>.</summary>
    public AiConfiguration(
        string provider,
        string model,
        double? temperature = null,
        int? maxTokens = null,
        string? processing = null,
        string? batchWindow = null,
        IReadOnlyList<AiFallbackConfiguration>? fallback = null,
        BudgetConfiguration? budget = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        Provider = provider;
        Model = model;
        Temperature = temperature;
        MaxTokens = maxTokens;
        Processing = processing;
        BatchWindow = batchWindow;
        Fallback = fallback ?? Array.Empty<AiFallbackConfiguration>();
        Budget = budget;
    }

    /// <summary>The provider registered in the AI gateway.</summary>
    public string Provider { get; }

    /// <summary>The model identifier requested from the provider.</summary>
    public string Model { get; }

    /// <summary>The optional sampling temperature.</summary>
    public double? Temperature { get; }

    /// <summary>The optional maximum number of tokens per call.</summary>
    public int? MaxTokens { get; }

    /// <summary>The processing mode as declared (for example <c>interactive</c> or <c>batch</c>).</summary>
    public string? Processing { get; }

    /// <summary>The optional batch window descriptor, relevant for batch processing.</summary>
    public string? BatchWindow { get; }

    /// <summary>The ordered fallback chain tried when the primary provider fails; empty when none.</summary>
    public IReadOnlyList<AiFallbackConfiguration> Fallback { get; }

    /// <summary>The optional spend/rate limits of the occupant.</summary>
    public BudgetConfiguration? Budget { get; }
}
