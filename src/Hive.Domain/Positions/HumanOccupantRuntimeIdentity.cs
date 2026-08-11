using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Runtime-only identity material for an active human occupation. The identity subsystem projects
/// these opaque references into the position configuration; personal channel endpoints never cross
/// this boundary.
/// </summary>
public sealed record HumanOccupantRuntimeIdentity
{
    public HumanOccupantRuntimeIdentity(
        UserId userId,
        OccupantChannelBindingId? channelBindingId = null)
    {
        UserId = userId ?? throw new ArgumentNullException(nameof(userId));
        ChannelBindingId = channelBindingId;
    }

    public UserId UserId { get; }

    public OccupantChannelBindingId? ChannelBindingId { get; }
}
