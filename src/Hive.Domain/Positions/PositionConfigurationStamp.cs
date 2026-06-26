namespace Hive.Domain.Positions;

/// <summary>
/// Versioned stamp for the runtime configuration accepted by a position entity (US-F0-06-T08a).
/// </summary>
public sealed record PositionConfigurationStamp
{
    public PositionConfigurationStamp(long version, string fingerprint)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "Configuration version must be positive.");
        }

        Version = version;
        Fingerprint = CommandText.RequireContent(fingerprint, nameof(fingerprint));
    }

    /// <summary>The monotonic registry/import version for the organization configuration.</summary>
    public long Version { get; }

    /// <summary>The canonical semantic fingerprint of the published runtime configuration.</summary>
    public string Fingerprint { get; }
}
