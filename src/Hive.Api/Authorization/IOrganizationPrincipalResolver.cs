using System.Security.Claims;
using Hive.Domain.Identity;

namespace Hive.Api.Authorization;

public interface IOrganizationPrincipalResolver
{
    ValueTask<OrganizationPrincipal> ResolveAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

public sealed class OrganizationPrincipal
{
    private readonly OrganizationId[] _organizationIds;

    public OrganizationPrincipal(IEnumerable<OrganizationId> organizationIds)
    {
        ArgumentNullException.ThrowIfNull(organizationIds);

        _organizationIds = organizationIds
            .Distinct()
            .OrderBy(static organizationId => organizationId.Value, StringComparer.Ordinal)
            .ToArray();
        OrganizationIds = Array.AsReadOnly(_organizationIds);
    }

    public IReadOnlyList<OrganizationId> OrganizationIds { get; }

    public bool CanRead(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        return Array.IndexOf(_organizationIds, organizationId) >= 0;
    }
}
