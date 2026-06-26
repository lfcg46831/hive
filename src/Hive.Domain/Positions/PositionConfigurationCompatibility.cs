using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Evaluates the US-F0-06-T08a compatibility matrix between recovered and current runtime
/// configuration stamps.
/// </summary>
public static class PositionConfigurationCompatibility
{
    public static PositionConfigurationCompatibilityResult Evaluate(
        PositionConfigurationStamp? recoveredStamp,
        PositionRuntimeConfigurationLoadResult loadResult,
        PositionEntityId entityId)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        ArgumentNullException.ThrowIfNull(entityId);

        return loadResult.Status switch
        {
            PositionRuntimeConfigurationLoadStatus.Loaded =>
                EvaluateLoaded(recoveredStamp, loadResult.Configuration!, entityId),
            PositionRuntimeConfigurationLoadStatus.Missing =>
                PositionConfigurationCompatibilityResult.Blocked(
                    PositionConfigurationBlockReason.ConfigurationMissing),
            PositionRuntimeConfigurationLoadStatus.Incomplete =>
                PositionConfigurationCompatibilityResult.Blocked(
                    PositionConfigurationBlockReason.ConfigurationIncomplete),
            PositionRuntimeConfigurationLoadStatus.InvalidStamp =>
                PositionConfigurationCompatibilityResult.Blocked(
                    PositionConfigurationBlockReason.InvalidStamp),
            PositionRuntimeConfigurationLoadStatus.UnsupportedRuntimeSchema =>
                PositionConfigurationCompatibilityResult.Blocked(
                    PositionConfigurationBlockReason.UnsupportedRuntimeSchema),
            PositionRuntimeConfigurationLoadStatus.TechnicalFailure =>
                PositionConfigurationCompatibilityResult.TechnicalFailure(loadResult.TechnicalException!),
            _ => throw new ArgumentOutOfRangeException(
                nameof(loadResult),
                loadResult.Status,
                "Unknown position runtime configuration load status."),
        };
    }

    private static PositionConfigurationCompatibilityResult EvaluateLoaded(
        PositionConfigurationStamp? recoveredStamp,
        PositionRuntimeConfiguration current,
        PositionEntityId entityId)
    {
        if (!current.Matches(entityId))
        {
            return PositionConfigurationCompatibilityResult.Blocked(
                PositionConfigurationBlockReason.EntityMismatch,
                current);
        }

        if (recoveredStamp is null)
        {
            return PositionConfigurationCompatibilityResult.ApplyNewConfiguration(current);
        }

        if (current.Stamp.Version == recoveredStamp.Version)
        {
            return string.Equals(
                current.Stamp.Fingerprint,
                recoveredStamp.Fingerprint,
                StringComparison.Ordinal)
                ? PositionConfigurationCompatibilityResult.AlreadyApplied(current)
                : PositionConfigurationCompatibilityResult.Blocked(
                    PositionConfigurationBlockReason.FingerprintChangedForVersion,
                    current);
        }

        return current.Stamp.Version > recoveredStamp.Version
            ? PositionConfigurationCompatibilityResult.ApplyNewConfiguration(current)
            : PositionConfigurationCompatibilityResult.Blocked(
                PositionConfigurationBlockReason.RecoveredVersionNewer,
                current);
    }
}

public sealed record PositionConfigurationCompatibilityResult
{
    private PositionConfigurationCompatibilityResult(
        PositionConfigurationCompatibilityDecision decision,
        PositionRuntimeConfiguration? configuration,
        PositionConfigurationBlockReason? blockReason,
        Exception? technicalException)
    {
        Decision = decision;
        Configuration = configuration;
        BlockReason = blockReason;
        TechnicalException = technicalException;
    }

    /// <summary>The compatibility decision the actor gate must honor.</summary>
    public PositionConfigurationCompatibilityDecision Decision { get; }

    /// <summary>The current configuration when available.</summary>
    public PositionRuntimeConfiguration? Configuration { get; }

    /// <summary>The fail-closed reason when <see cref="Decision"/> is <see cref="PositionConfigurationCompatibilityDecision.Blocked"/>.</summary>
    public PositionConfigurationBlockReason? BlockReason { get; }

    /// <summary>The technical failure when <see cref="Decision"/> is <see cref="PositionConfigurationCompatibilityDecision.TechnicalFailure"/>.</summary>
    public Exception? TechnicalException { get; }

    public static PositionConfigurationCompatibilityResult ApplyNewConfiguration(
        PositionRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new PositionConfigurationCompatibilityResult(
            PositionConfigurationCompatibilityDecision.ApplyNewConfiguration,
            configuration,
            blockReason: null,
            technicalException: null);
    }

    public static PositionConfigurationCompatibilityResult AlreadyApplied(
        PositionRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new PositionConfigurationCompatibilityResult(
            PositionConfigurationCompatibilityDecision.AlreadyApplied,
            configuration,
            blockReason: null,
            technicalException: null);
    }

    public static PositionConfigurationCompatibilityResult Blocked(
        PositionConfigurationBlockReason reason,
        PositionRuntimeConfiguration? configuration = null)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Unknown configuration block reason.");
        }

        return new PositionConfigurationCompatibilityResult(
            PositionConfigurationCompatibilityDecision.Blocked,
            configuration,
            reason,
            technicalException: null);
    }

    public static PositionConfigurationCompatibilityResult TechnicalFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new PositionConfigurationCompatibilityResult(
            PositionConfigurationCompatibilityDecision.TechnicalFailure,
            configuration: null,
            blockReason: null,
            technicalException: exception);
    }
}

/// <summary>Decisions produced by the runtime-configuration compatibility matrix.</summary>
public enum PositionConfigurationCompatibilityDecision
{
    ApplyNewConfiguration = 1,
    AlreadyApplied = 2,
    Blocked = 3,
    TechnicalFailure = 4,
}

/// <summary>Fail-closed reasons for blocking a position configuration gate.</summary>
public enum PositionConfigurationBlockReason
{
    ConfigurationMissing = 1,
    ConfigurationIncomplete = 2,
    InvalidStamp = 3,
    UnsupportedRuntimeSchema = 4,
    EntityMismatch = 5,
    FingerprintChangedForVersion = 6,
    RecoveredVersionNewer = 7,
}
