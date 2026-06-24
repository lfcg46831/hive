using Hive.Domain.Identity;

namespace Hive.Api.Organization;

public static class OrganizationRegistryEndpointExtensions
{
    public const string BasePath = "/internal/organizations";

    public static IEndpointRouteBuilder MapHiveOrganizationRegistryApi(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(BasePath);
        group.MapGet("/{organizationId}", GetOrganizationAsync);
        group.MapGet("/{organizationId}/units", GetUnitsAsync);
        group.MapGet("/{organizationId}/positions", GetPositionsAsync);
        group.MapGet("/{organizationId}/command-relations", GetCommandRelationsAsync);
        group.MapGet(
            "/{organizationId}/positions/{positionId}/configuration",
            GetPositionConfigurationAsync);
        return endpoints;
    }

    private static async Task<IResult> GetOrganizationAsync(
        string organizationId,
        OrganizationRegistryApiReader source,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var parsedOrganizationId))
        {
            return InvalidOrganizationId();
        }

        if (source.Reader is null)
        {
            return RegistryUnavailable();
        }

        var snapshot = await source.Reader.FindSnapshotAsync(
            parsedOrganizationId!,
            cancellationToken);
        return snapshot is null
            ? OrganizationNotFound()
            : TypedResults.Ok(OrganizationRegistryResponseMapper.MapOrganization(snapshot));
    }

    private static async Task<IResult> GetUnitsAsync(
        string organizationId,
        OrganizationRegistryApiReader source,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var parsedOrganizationId))
        {
            return InvalidOrganizationId();
        }

        if (source.Reader is null)
        {
            return RegistryUnavailable();
        }

        var snapshot = await source.Reader.FindSnapshotAsync(
            parsedOrganizationId!,
            cancellationToken);
        return snapshot is null
            ? OrganizationNotFound()
            : TypedResults.Ok(OrganizationRegistryResponseMapper.MapUnits(snapshot));
    }

    private static async Task<IResult> GetPositionsAsync(
        string organizationId,
        OrganizationRegistryApiReader source,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var parsedOrganizationId))
        {
            return InvalidOrganizationId();
        }

        if (source.Reader is null)
        {
            return RegistryUnavailable();
        }

        var snapshot = await source.Reader.FindSnapshotAsync(
            parsedOrganizationId!,
            cancellationToken);
        return snapshot is null
            ? OrganizationNotFound()
            : TypedResults.Ok(OrganizationRegistryResponseMapper.MapPositions(snapshot));
    }

    private static async Task<IResult> GetCommandRelationsAsync(
        string organizationId,
        OrganizationRegistryApiReader source,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var parsedOrganizationId))
        {
            return InvalidOrganizationId();
        }

        if (source.Reader is null)
        {
            return RegistryUnavailable();
        }

        var snapshot = await source.Reader.FindSnapshotAsync(
            parsedOrganizationId!,
            cancellationToken);
        return snapshot is null
            ? OrganizationNotFound()
            : TypedResults.Ok(OrganizationRegistryResponseMapper.MapCommandRelations(snapshot));
    }

    private static async Task<IResult> GetPositionConfigurationAsync(
        string organizationId,
        string positionId,
        OrganizationRegistryApiReader source,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var parsedOrganizationId))
        {
            return InvalidOrganizationId();
        }

        if (!TryParsePositionId(positionId, out var parsedPositionId))
        {
            return InvalidPositionId();
        }

        if (source.Reader is null)
        {
            return RegistryUnavailable();
        }

        var snapshot = await source.Reader.FindSnapshotAsync(
            parsedOrganizationId!,
            cancellationToken);
        if (snapshot is null)
        {
            return OrganizationNotFound();
        }

        return snapshot.Positions.ContainsKey(parsedPositionId!)
            ? TypedResults.Ok(
                OrganizationRegistryResponseMapper.MapPositionConfiguration(
                    snapshot,
                    parsedPositionId!))
            : PositionNotFound();
    }

    private static IResult RegistryUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization registry unavailable");

    private static IResult InvalidOrganizationId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid organization identifier");

    private static IResult InvalidPositionId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid position identifier");

    private static IResult OrganizationNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Organization not found");

    private static IResult PositionNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Position not found");

    private static bool TryParseOrganizationId(
        string value,
        out OrganizationId? organizationId)
    {
        try
        {
            organizationId = OrganizationId.From(value);
            return true;
        }
        catch (ArgumentException)
        {
            organizationId = null;
            return false;
        }
    }

    private static bool TryParsePositionId(string value, out PositionId? positionId)
    {
        try
        {
            positionId = PositionId.From(value);
            return true;
        }
        catch (ArgumentException)
        {
            positionId = null;
            return false;
        }
    }
}
