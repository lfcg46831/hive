using Akka.Actor;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;

namespace Hive.Actors.Positions;

internal interface IPositionOccupantFactory
{
    Props Create(OccupantId occupant, OccupantType occupantType);
}

internal sealed class PositionOccupantFactory : IPositionOccupantFactory
{
    public static PositionOccupantFactory Instance { get; } = new();

    public Props Create(OccupantId occupant, OccupantType occupantType)
    {
        ArgumentNullException.ThrowIfNull(occupant);

        return occupantType switch
        {
            OccupantType.AiAgent => Props.Create(() => new AiAgentActor(occupant)),
            OccupantType.Human => Props.Create(() => new HumanProxyActor(occupant)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(occupantType),
                occupantType,
                "Occupant type must be AiAgent or Human."),
        };
    }
}

internal sealed class AiAgentActor : ReceiveActor
{
    public AiAgentActor(OccupantId occupant)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));

        Receive<OrgMessage>(_ =>
        {
        });
    }

    public OccupantId Occupant { get; }
}

internal sealed class HumanProxyActor : ReceiveActor
{
    public HumanProxyActor(OccupantId occupant)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));

        Receive<OrgMessage>(_ =>
        {
        });
    }

    public OccupantId Occupant { get; }
}
