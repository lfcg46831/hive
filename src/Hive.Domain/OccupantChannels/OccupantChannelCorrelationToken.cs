namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Opaque correlation material rendered into a channel notification. Creation and validation of
/// signed tokens belong to the correlation-token service, not to this transport contract.
/// </summary>
public sealed record OccupantChannelCorrelationToken
{
    private OccupantChannelCorrelationToken(string value) => Value = value;

    public string Value { get; }

    public static OccupantChannelCorrelationToken From(string value) =>
        new(OccupantChannelContractGuards.RequireOpaqueToken(value, nameof(value)));

    public override string ToString() => "[REDACTED]";
}
