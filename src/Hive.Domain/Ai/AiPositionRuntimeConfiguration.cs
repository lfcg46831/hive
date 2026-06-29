using System.Collections.Immutable;

namespace Hive.Domain.Ai;

public sealed record AiPositionRuntimeConfiguration
{
    public AiPositionRuntimeConfiguration(
        AiProviderMetadata primary,
        AiModelParameters? parameters = null,
        TimeSpan? timeout = null,
        AiProcessingMode? processingMode = null,
        IEnumerable<AiProviderMetadata>? fallback = null,
        AiCostLimits? costLimits = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "AI timeout must be greater than zero.");
        }

        if (processingMode is { } mode)
        {
            AiProcessingModeContract.RequireDefined(mode, nameof(processingMode));
        }

        Primary = primary;
        Parameters = parameters ?? AiModelParameters.Default;
        Timeout = timeout;
        ProcessingMode = processingMode;
        Fallback = AiContractGuards.Snapshot(fallback, nameof(fallback));
        CostLimits = costLimits;
    }

    public AiProviderMetadata Primary { get; }

    public AiModelParameters Parameters { get; }

    public TimeSpan? Timeout { get; }

    public AiProcessingMode? ProcessingMode { get; }

    public ImmutableArray<AiProviderMetadata> Fallback { get; }

    public AiCostLimits? CostLimits { get; }
}
