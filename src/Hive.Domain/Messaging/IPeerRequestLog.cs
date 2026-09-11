using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

/// <summary>Read-only correlation over durable requests owned by the requesting position.</summary>
/// <remarks>
/// Null confirms absence within the organization/requester scope. Cancellation and technical
/// failures propagate; reads never register, close or admit messages.
/// </remarks>
public interface IPeerRequestLog
{
    ValueTask<PeerRequestRecord?> FindRequestAsync(
        OrganizationId organizationId,
        PositionId requester,
        MessageId requestId,
        CancellationToken cancellationToken = default);
}
