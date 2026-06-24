using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// The occupant filling the position was replaced — the fact produced by a successful
/// <see cref="ChangeOccupant"/>. The position is stable; only the occupant changes (§4.x), so the
/// inbox, tasks and history owned by the position are unaffected. Replay restores the current
/// occupant from the latest such event.
/// </summary>
public sealed record OccupantChanged : PositionEvent
{
    public OccupantChanged(OccupantId occupant, OccupantType type, DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(occupant);

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Occupant type must be AiAgent or Human.");
        }

        Occupant = occupant;
        Type = type;
    }

    /// <summary>The identity of the new occupant.</summary>
    public OccupantId Occupant { get; }

    /// <summary>Whether the new occupant is an AI agent or a human.</summary>
    public OccupantType Type { get; }
}
