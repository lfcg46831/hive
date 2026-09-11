using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

/// <summary>The directional channel resolved at admission, retained even if positions move units.</summary>
public sealed record PeerRequestChannel
{
    public PeerRequestChannel(UnitId fromUnit, UnitId toUnit, int maxOpenRequests)
    {
        FromUnit = fromUnit ?? throw new ArgumentNullException(nameof(fromUnit));
        ToUnit = toUnit ?? throw new ArgumentNullException(nameof(toUnit));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOpenRequests);
        if (fromUnit == toUnit) throw new ArgumentException("A declared channel requires different units.");
        MaxOpenRequests = maxOpenRequests;
    }

    public UnitId FromUnit { get; }
    public UnitId ToUnit { get; }
    public int MaxOpenRequests { get; }
}
