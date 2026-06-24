using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record OrganizationImportPlan(
    OrganizationId OrganizationId,
    string Fingerprint,
    long TargetVersion,
    IReadOnlyList<OrganizationRegistryChange> Changes);
