namespace Hive.Infrastructure.Organization.Registry;

public sealed record OrganizationRegistryChange(
    RegistryEntityKind EntityKind,
    string Key,
    RegistryChangeKind Kind);
