using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

/// <summary>
/// Receives best-effort notifications after durable organization read-model changes commit.
/// Consumers must not let notification failures fail the owning durable write.
/// </summary>
public interface IOrganizationReadModelChangeSink
{
    ValueTask OrganogramChangedAsync(
        OrganizationId organizationId,
        long registryVersion,
        string registryFingerprint,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask PositionStateChangedAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default);
}
