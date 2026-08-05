namespace Hive.Api.Authorization;

public sealed class OrganizationAuthorizationOptions
{
    public const string SectionName = "Hive:PublicApi:Authorization";

    public List<OrganizationBearerCredentialOptions> Credentials { get; set; } = [];
}

public sealed class OrganizationBearerCredentialOptions
{
    public string Token { get; set; } = string.Empty;

    public List<string> OrganizationIds { get; set; } = [];

    public string? PersonId { get; set; }

    public List<OccupiedPositionOptions> Positions { get; set; } = [];
}

public sealed class OccupiedPositionOptions
{
    public string OrganizationId { get; set; } = string.Empty;

    public string PositionId { get; set; } = string.Empty;
}
