namespace Hive.Api.Authorization;

public static class OrganizationAuthorizationDefaults
{
    public const string AuthenticationScheme = "HiveOrganizationBearer";

    public const string Policy = "OrganizationRead";

    internal const string OrganizationClaimType = "hive:organization";
}
