using Hive.Domain.Organization.Configuration.Validation;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record OrganizationImportResult
{
    internal OrganizationImportResult(
        OrganizationImportStatus status,
        OrganizationImportPlan? plan,
        OrganizationRegistrySnapshot? snapshot,
        IReadOnlyList<OrganizationConfigurationValidationError>? validationErrors = null)
    {
        Status = status;
        Plan = plan;
        Snapshot = snapshot;
        ValidationErrors = validationErrors ?? Array.Empty<OrganizationConfigurationValidationError>();
    }

    public OrganizationImportStatus Status { get; }

    public OrganizationImportPlan? Plan { get; }

    public OrganizationRegistrySnapshot? Snapshot { get; }

    public IReadOnlyList<OrganizationConfigurationValidationError> ValidationErrors { get; }
}
