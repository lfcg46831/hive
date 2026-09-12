using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>Internal, sanitized notification of a rejection with a captured escalate policy.</summary>
public sealed record RecordPeerRequestRejection : PositionCommand
{
    public RecordPeerRequestRejection(PeerRequest request, RejectionReason reason)
    {
        _ = new PeerRequestRecord(request, MessageState.Accepted);
        if (reason is not (RejectionReason.InvalidRoute or RejectionReason.LimitExceeded))
            throw new ArgumentOutOfRangeException(nameof(reason));
        Request = request;
        Reason = reason;
    }

    public PeerRequest Request { get; }
    public RejectionReason Reason { get; }

    public MessageId EscalationId => MessageId.From(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"peer-rejection:v1\n{Request.OrganizationId.Value}\n{((PositionEndpointRef)Request.From).PositionId.Value}\n{Request.Thread.Value}\n{RejectionReasonContract.ToWireValue(Reason)}"))[..16]));
}
