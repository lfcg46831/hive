using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public abstract record EndpointRef;

public sealed record PositionEndpointRef : EndpointRef
{
    public PositionEndpointRef(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        PositionId = positionId;
    }

    public PositionId PositionId { get; }
}

public sealed record OrganizationOwnerEndpointRef : EndpointRef;

public sealed record SystemEndpointRef : EndpointRef
{
    public SystemEndpointRef(SystemEndpointKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "System endpoint must be Scheduler or DomainEvents.");
        }

        Kind = kind;
    }

    public SystemEndpointKind Kind { get; }
}
