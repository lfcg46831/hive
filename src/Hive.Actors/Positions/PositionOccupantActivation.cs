using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Actors.Positions;

/// <summary>
/// Runtime proof required before the position factory may materialize an occupant child. Human
/// activations carry the user identity supplied by an active occupation link.
/// </summary>
internal sealed record PositionOccupantActivation
{
    private PositionOccupantActivation(
        OccupantId occupant,
        OccupantType occupantType,
        UserId? userId)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        OccupantType = occupantType;
        UserId = userId;
    }

    public OccupantId Occupant { get; }

    public OccupantType OccupantType { get; }

    public UserId? UserId { get; }

    public static PositionOccupantActivation AiAgent(OccupantId occupant) =>
        new(occupant, OccupantType.AiAgent, userId: null);

    public static PositionOccupantActivation Human(OccupantId occupant, UserId userId) =>
        new(
            occupant,
            OccupantType.Human,
            userId ?? throw new ArgumentNullException(nameof(userId)));
}
