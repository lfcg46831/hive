using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryOrganization(
    OrganizationId Id,
    string? Name,
    UnitId RootUnit,
    OwnerConfiguration Owner,
    IReadOnlyList<PromptConfiguration> Prompts);
