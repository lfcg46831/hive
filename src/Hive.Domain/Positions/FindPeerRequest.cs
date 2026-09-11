using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>Read-only query delivered to the original requester through sharding.</summary>
public sealed record FindPeerRequest : PositionCommand
{
    public FindPeerRequest(MessageId requestId)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
    }

    public MessageId RequestId { get; }
}

/// <summary>A wrapper allows confirmed absence to travel as a non-null actor reply.</summary>
public sealed record PeerRequestLookupResult(PeerRequestRecord? Record);
