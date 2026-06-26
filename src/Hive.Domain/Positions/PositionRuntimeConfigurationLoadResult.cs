namespace Hive.Domain.Positions;

/// <summary>
/// Outcome of loading runtime configuration for a position entity from the registry/read model
/// (US-F0-06-T08a).
/// </summary>
public sealed record PositionRuntimeConfigurationLoadResult
{
    private PositionRuntimeConfigurationLoadResult(
        PositionRuntimeConfigurationLoadStatus status,
        PositionRuntimeConfiguration? configuration,
        string? reason,
        Exception? technicalException)
    {
        Status = status;
        Configuration = configuration;
        Reason = reason;
        TechnicalException = technicalException;
    }

    /// <summary>The coarse outcome category.</summary>
    public PositionRuntimeConfigurationLoadStatus Status { get; }

    /// <summary>The loaded runtime configuration when <see cref="Status"/> is <see cref="PositionRuntimeConfigurationLoadStatus.Loaded"/>.</summary>
    public PositionRuntimeConfiguration? Configuration { get; }

    /// <summary>The blocking reason supplied by the provider, when applicable.</summary>
    public string? Reason { get; }

    /// <summary>The technical failure that should be retried or supervised, when applicable.</summary>
    public Exception? TechnicalException { get; }

    /// <summary>True when the result is a fail-closed configuration problem, not a transient failure.</summary>
    public bool IsBlocking => Status is
        PositionRuntimeConfigurationLoadStatus.Missing or
        PositionRuntimeConfigurationLoadStatus.Incomplete or
        PositionRuntimeConfigurationLoadStatus.InvalidStamp or
        PositionRuntimeConfigurationLoadStatus.UnsupportedRuntimeSchema;

    /// <summary>True when the registry/read model failed technically and should not become a routing rejection.</summary>
    public bool IsTechnicalFailure => Status == PositionRuntimeConfigurationLoadStatus.TechnicalFailure;

    public static PositionRuntimeConfigurationLoadResult Loaded(PositionRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new PositionRuntimeConfigurationLoadResult(
            PositionRuntimeConfigurationLoadStatus.Loaded,
            configuration,
            reason: null,
            technicalException: null);
    }

    public static PositionRuntimeConfigurationLoadResult Missing(string reason) =>
        Blocking(PositionRuntimeConfigurationLoadStatus.Missing, reason);

    public static PositionRuntimeConfigurationLoadResult Incomplete(string reason) =>
        Blocking(PositionRuntimeConfigurationLoadStatus.Incomplete, reason);

    public static PositionRuntimeConfigurationLoadResult InvalidStamp(string reason) =>
        Blocking(PositionRuntimeConfigurationLoadStatus.InvalidStamp, reason);

    public static PositionRuntimeConfigurationLoadResult UnsupportedRuntimeSchema(string reason) =>
        Blocking(PositionRuntimeConfigurationLoadStatus.UnsupportedRuntimeSchema, reason);

    public static PositionRuntimeConfigurationLoadResult TechnicalFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new PositionRuntimeConfigurationLoadResult(
            PositionRuntimeConfigurationLoadStatus.TechnicalFailure,
            configuration: null,
            reason: null,
            technicalException: exception);
    }

    private static PositionRuntimeConfigurationLoadResult Blocking(
        PositionRuntimeConfigurationLoadStatus status,
        string reason) =>
        new(
            status,
            configuration: null,
            CommandText.RequireContent(reason, nameof(reason)),
            technicalException: null);
}

/// <summary>Coarse result categories for loading position runtime configuration.</summary>
public enum PositionRuntimeConfigurationLoadStatus
{
    Loaded = 1,
    Missing = 2,
    Incomplete = 3,
    InvalidStamp = 4,
    UnsupportedRuntimeSchema = 5,
    TechnicalFailure = 6,
}
