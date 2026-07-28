using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryOrganization(
    OrganizationId Id,
    string? Name,
    UnitId RootUnit,
    OwnerConfiguration Owner,
    IReadOnlyList<PromptConfiguration> Prompts,
    OutcomePolicyOverlay? OutcomePolicy);
