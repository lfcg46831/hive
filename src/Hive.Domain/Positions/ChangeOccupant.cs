using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// Replace the occupant currently filling the position. The position is stable; only the occupant
/// changes (§4.x: trocar um agente por uma pessoa é trocar o ocupante, não a posição), so the inbox,
/// tasks and history — owned by the position — are unaffected by this command.
/// </summary>
public sealed record ChangeOccupant : PositionCommand
{
    public ChangeOccupant(OccupantId occupant, OccupantType type)
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
