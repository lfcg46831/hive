namespace Hive.Infrastructure.Organization.Registry;

internal interface IOrganizationRegistryStore
{
    ValueTask<OrganizationImportResult> ApplyAsync(
        OrganizationRegistryProjection target,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken);
}
