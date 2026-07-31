using System.Collections.Immutable;

namespace Hive.Domain.Ai;

public sealed record AiPositionRuntimeConfiguration
{
    public const int LegacyLimitsVersion = 0;
    public const int CurrentLimitsVersion = 1;

    public AiPositionRuntimeConfiguration(
        AiProviderMetadata primary,
        AiModelParameters? parameters = null,
        TimeSpan? timeout = null,
        AiProcessingMode? processingMode = null,
        IEnumerable<AiProviderMetadata>? fallback = null,
        AiCostLimits? costLimits = null,
        int? maxIterations = null,
        int limitsVersion = LegacyLimitsVersion,
        TimeSpan? executionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "AI timeout must be greater than zero.");
        }

        if (maxIterations is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIterations),
                maxIterations,
                "AI max iterations must be greater than zero.");
        }

        if (limitsVersion is not LegacyLimitsVersion and not CurrentLimitsVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limitsVersion),
                limitsVersion,
                "AI execution limits version is not supported.");
        }

        if (executionTimeout is { } executionTimeoutValue &&
            executionTimeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionTimeout),
                executionTimeout,
                "AI execution timeout must be greater than zero.");
        }

        if (limitsVersion == CurrentLimitsVersion &&
            (timeout is null || executionTimeout is null))
        {
            throw new ArgumentException(
                "AI execution limits version 1 requires both per-call and end-to-end timeouts.",
                nameof(executionTimeout));
        }

        if (limitsVersion == LegacyLimitsVersion && executionTimeout is not null)
        {
            throw new ArgumentException(
                "A separate AI execution timeout requires execution limits version 1.",
                nameof(executionTimeout));
        }

        if (processingMode is { } mode)
        {
            AiProcessingModeContract.RequireDefined(mode, nameof(processingMode));
        }

        Primary = primary;
        Parameters = parameters ?? AiModelParameters.Default;
        Timeout = timeout;
        LimitsVersion = limitsVersion;
        ExecutionTimeout = limitsVersion == LegacyLimitsVersion
            ? timeout
            : executionTimeout;
        ProcessingMode = processingMode;
        Fallback = AiContractGuards.Snapshot(fallback, nameof(fallback));
        CostLimits = costLimits;
        MaxIterations = maxIterations;
    }

    public AiProviderMetadata Primary { get; }

    public AiModelParameters Parameters { get; }

    public TimeSpan? Timeout { get; }

    /// <summary>The maximum duration of each provider, connector, or verifier call.</summary>
    public TimeSpan? PerCallTimeout => Timeout;

    /// <summary>The immutable end-to-end budget for one directive execution.</summary>
    public TimeSpan? ExecutionTimeout { get; }

    /// <summary>
    /// Version 0 preserves the historical shared-timeout contract; version 1 separates per-call
    /// and end-to-end limits.
    /// </summary>
    public int LimitsVersion { get; }

    public AiProcessingMode? ProcessingMode { get; }

    public ImmutableArray<AiProviderMetadata> Fallback { get; }

    public AiCostLimits? CostLimits { get; }

    public int? MaxIterations { get; }
}
