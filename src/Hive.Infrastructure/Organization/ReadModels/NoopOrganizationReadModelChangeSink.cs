using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

internal sealed class NoopOrganizationReadModelChangeSink : IOrganizationReadModelChangeSink
{
    public static readonly NoopOrganizationReadModelChangeSink Instance = new();

    private NoopOrganizationReadModelChangeSink()
    {
    }

    public ValueTask OrganogramChangedAsync(
        OrganizationId organizationId,
        long registryVersion,
        string registryFingerprint,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask PositionStateChangedAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
