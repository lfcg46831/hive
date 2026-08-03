using System.Security.Claims;
using Hive.Domain.Identity;

namespace Hive.Api.Authorization;

internal sealed class ClaimsOrganizationPrincipalResolver : IOrganizationPrincipalResolver
{
    public ValueTask<OrganizationPrincipal> ResolveAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();

        var organizationIds = principal
            .FindAll(OrganizationAuthorizationDefaults.OrganizationClaimType)
            .Select(static claim => TryParseOrganizationId(claim.Value))
            .Where(static organizationId => organizationId is not null)
            .Cast<OrganizationId>();
        return ValueTask.FromResult(new OrganizationPrincipal(organizationIds));
    }

    private static OrganizationId? TryParseOrganizationId(string value)
    {
        try
        {
            return OrganizationId.From(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
