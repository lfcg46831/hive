using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>Recipient-side capacity record, independent of delivery history and requester-side correlation.</summary>
public sealed record ReceivedPeerRequest
{
    public ReceivedPeerRequest(PeerRequest request, PeerRequestChannel channel, bool responded = false)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        if (request.From is not PositionEndpointRef || request.To is not PositionEndpointRef)
            throw new ArgumentException("Peer requests require position endpoints.", nameof(request));
        Responded = responded;
    }
    public PeerRequest Request { get; }
    public PeerRequestChannel Channel { get; }
    public bool Responded { get; }
}
