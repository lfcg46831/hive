namespace Hive.Domain.Positions;

/// <summary>
/// The position accepted a runtime configuration stamp from the registry/read model
/// (US-F0-06-T08c). Replaying this fact restores the latest applied stamp without relying on
/// snapshot metadata alone.
/// </summary>
public sealed record PositionConfigurationApplied : PositionEvent
{
    public PositionConfigurationApplied(PositionConfigurationStamp stamp, DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(stamp);

        Stamp = stamp;
    }

    /// <summary>The runtime configuration stamp accepted by the position entity.</summary>
    public PositionConfigurationStamp Stamp { get; }
}
