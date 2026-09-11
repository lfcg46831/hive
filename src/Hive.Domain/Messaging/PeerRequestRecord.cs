namespace Hive.Domain.Messaging;

/// <summary>The original admitted request and its durable correlation lifecycle.</summary>
public sealed record PeerRequestRecord
{
    public PeerRequestRecord(PeerRequest request, MessageState state)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From is not PositionEndpointRef || request.To is not PositionEndpointRef)
        {
            throw new ArgumentException("Peer requests require position endpoints.", nameof(request));
        }

        Request = request;
        State = MessageStateContract.RequireDefined(state, nameof(state));
    }

    public PeerRequest Request { get; }
    public MessageState State { get; }
}
