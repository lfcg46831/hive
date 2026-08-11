using Hive.Domain.OccupantChannels;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

/// <summary>
/// Ephemeral payload handed to a human proxy only after the corresponding dispatch event has been
/// confirmed by the position journal.
/// </summary>
internal sealed record HumanOccupantChannelDelivery
{
    public HumanOccupantChannelDelivery(
        MessageDispatched dispatch,
        OccupantChannelDeliveryContext context)
    {
        Dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        Context = context ?? throw new ArgumentNullException(nameof(context));

        if (Dispatch.Message != Context.Message.Id ||
            Dispatch.Thread != Context.Message.Thread ||
            Dispatch.Occupant != Context.OccupantId ||
            Dispatch.OccupantType != Domain.Organization.Configuration.OccupantType.Human)
        {
            throw new ArgumentException(
                "Human occupant delivery context must match the persisted dispatch.",
                nameof(context));
        }
    }

    public MessageDispatched Dispatch { get; }

    public OccupantChannelDeliveryContext Context { get; }
}
