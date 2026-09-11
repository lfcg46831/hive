using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>Internal intent to retain an admitted outgoing request at its requester.</summary>
public sealed record RecordPeerRequest : PositionCommand
{
    public RecordPeerRequest(PeerRequest request)
    {
        _ = new PeerRequestRecord(request, MessageState.Accepted);
        Request = request;
    }

    public PeerRequest Request { get; }
}
