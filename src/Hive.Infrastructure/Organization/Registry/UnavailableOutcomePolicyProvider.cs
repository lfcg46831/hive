using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class UnavailableOutcomePolicyProvider(string connectionStringName)
    : IOutcomePolicyProvider
{
    public ValueTask<OutcomePolicySnapshot> GetPolicyAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OutcomePolicySnapshot>(new InvalidOperationException(
            $"Outcome policy is unavailable because connection string '{connectionStringName}' is not configured."));
}
