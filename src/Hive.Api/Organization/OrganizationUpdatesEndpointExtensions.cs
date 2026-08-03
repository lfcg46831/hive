using Hive.Api.Authorization;

namespace Hive.Api.Organization;

public static class OrganizationUpdatesEndpointExtensions
{
    public const string HubPath = "/api/v1/organization-updates";

    public static IEndpointRouteBuilder MapHiveOrganizationUpdatesHub(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHub<OrganizationUpdatesHub>(HubPath)
            .RequireAuthorization(OrganizationAuthorizationDefaults.Policy);
        return endpoints;
    }
}
