using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.ReadModels;
using Microsoft.AspNetCore.SignalR;

namespace Hive.Api.Organization;

internal sealed class SignalROrganizationReadModelChangeSink(
    IHubContext<OrganizationUpdatesHub, IOrganizationUpdatesClient> hubContext,
    IOrganizationReadModel readModel,
    ILogger<SignalROrganizationReadModelChangeSink> logger) :
    IOrganizationReadModelChangeSink
{
    public async ValueTask OrganogramChangedAsync(
        OrganizationId organizationId,
        long registryVersion,
        string registryFingerprint,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        try
        {
            var notification = new OrganogramChangedNotification(
                organizationId.Value,
                new RegistryVersion(registryVersion, registryFingerprint),
                changedAtUtc.ToUniversalTime());
            await hubContext.Clients
                .Group(OrganizationUpdatesHub.GroupName(organizationId))
                .OrganogramChanged(notification)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Realtime delivery is best effort; the REST snapshot remains authoritative.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not publish the organogram change for organization {OrganizationId}; clients will reconcile through REST polling.",
                organizationId.Value);
        }
    }

    public async ValueTask PositionStateChangedAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        try
        {
            var result = await readModel.ReadPositionAsync(
                organizationId,
                positionId,
                cancellationToken);
            var state = result.Value?.Position.OperationalState;
            if (!result.IsAvailable || state is null)
            {
                logger.LogWarning(
                    "Could not resolve the committed state for position {PositionId} in organization {OrganizationId}; clients will reconcile through REST polling.",
                    positionId.Value,
                    organizationId.Value);
                return;
            }

            var notification = new PositionStateChangedNotification(
                organizationId.Value,
                state);
            await hubContext.Clients
                .Group(OrganizationUpdatesHub.GroupName(organizationId))
                .PositionStateChanged(notification)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Realtime delivery is best effort; the REST snapshot remains authoritative.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not publish the state change for position {PositionId} in organization {OrganizationId}; clients will reconcile through REST polling.",
                positionId.Value,
                organizationId.Value);
        }
    }
}
