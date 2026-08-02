using System.Security.Cryptography;
using System.Text;
using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Microsoft.Net.Http.Headers;

namespace Hive.Api.Organization;

public static class OrganizationEndpointExtensions
{
    public const string BasePath = "/api/v1/organizations";

    public const string OrganogramRoute = "/{organizationId}/organogram";

    public const string UnitOrganogramRoute = "/{organizationId}/units/{unitId}/organogram";

    public const string PositionRoute = "/{organizationId}/positions/{positionId}";

    public const string PositionStatesRoute = "/{organizationId}/position-states";

    public static IEndpointRouteBuilder MapHiveOrganizationApi(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(BasePath)
            .WithTags("Organization");
        group.MapGet(OrganogramRoute, ReadOrganogramAsync)
            .WithName("GetOrganizationOrganogramV1")
            .WithSummary("Get the complete organization organogram")
            .WithDescription(
                "Returns the complete, deterministically ordered organogram snapshot. " +
                "Pagination and query filtering do not apply to this snapshot resource.")
            .Produces<OrganogramResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet(UnitOrganogramRoute, ReadUnitOrganogramAsync)
            .WithName("GetOrganizationUnitOrganogramV1")
            .WithSummary("Get an organogram subtree rooted at a unit")
            .WithDescription(
                "Returns the complete, deterministically ordered subtree for the requested unit. " +
                "The unit route is the supported subtree filter; pagination and query filtering do not apply.")
            .Produces<OrganogramResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet(PositionRoute, ReadPositionAsync)
            .WithName("GetOrganizationPositionV1")
            .WithSummary("Get organization position details")
            .WithDescription(
                "Returns one position with its occupant, direct hierarchy and latest correlated operational event. " +
                "Pagination and query filtering do not apply to this resource.")
            .Produces<PositionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet(PositionStatesRoute, ReadPositionStatesAsync)
            .WithName("GetOrganizationPositionStatesV1")
            .WithSummary("Get the organization position-state snapshot")
            .WithDescription(
                "Returns the complete, deterministically ordered state snapshot used for controlled polling. " +
                "Use If-None-Match with the response ETag to avoid transferring an unchanged snapshot. " +
                "Pagination and query filtering do not apply.")
            .Produces<PositionStatesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> ReadOrganogramAsync(
        string organizationId,
        IOrganizationReadModel readModel,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        var result = await readModel.ReadOrganogramAsync(
                organization!,
                rootUnitId: null,
                cancellationToken)
            .ConfigureAwait(false);
        return MapReadResult(result, OrganizationNotFound);
    }

    private static async Task<IResult> ReadUnitOrganogramAsync(
        string organizationId,
        string unitId,
        IOrganizationReadModel readModel,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        if (!TryParseUnitId(unitId, out var unit))
        {
            return InvalidUnitId();
        }

        var result = await readModel.ReadOrganogramAsync(
                organization!,
                unit,
                cancellationToken)
            .ConfigureAwait(false);
        return MapReadResult(result, UnitNotFound);
    }

    private static async Task<IResult> ReadPositionAsync(
        string organizationId,
        string positionId,
        IOrganizationReadModel readModel,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        if (!TryParsePositionId(positionId, out var position))
        {
            return InvalidPositionId();
        }

        var result = await readModel.ReadPositionAsync(
                organization!,
                position!,
                cancellationToken)
            .ConfigureAwait(false);
        return MapReadResult(result, PositionNotFound);
    }

    private static async Task<IResult> ReadPositionStatesAsync(
        string organizationId,
        HttpContext context,
        IOrganizationReadModel readModel,
        CancellationToken cancellationToken)
    {
        if (!TryParseOrganizationId(organizationId, out var organization))
        {
            return InvalidOrganizationId();
        }

        var result = await readModel.ReadPositionStatesAsync(
                organization!,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        if (result.Value is not { } response)
        {
            return OrganizationNotFound();
        }

        var entityTag = CreatePositionStatesEntityTag(response);
        context.Response.Headers.ETag = entityTag;
        if (MatchesIfNoneMatch(context.Request, entityTag))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Ok(response);
    }

    private static IResult MapReadResult<T>(
        OrganizationReadResult<T> result,
        Func<IResult> notFound)
        where T : class
    {
        if (!result.IsAvailable)
        {
            return ReadModelUnavailable();
        }

        return result.Value is { } value
            ? TypedResults.Ok(value)
            : notFound();
    }

    private static string CreatePositionStatesEntityTag(PositionStatesResponse response)
    {
        var versionVector = new StringBuilder()
            .Append(response.Registry.Version)
            .Append('|')
            .Append(response.Registry.Fingerprint)
            .Append('|')
            .Append(response.LastEventAppliedAtUtc?.UtcTicks ?? 0);
        foreach (var state in response.States)
        {
            versionVector
                .Append('|')
                .Append(state.PositionId)
                .Append(':')
                .Append(state.Sequence);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(versionVector.ToString()));
        return $"W/\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }

    private static bool MatchesIfNoneMatch(HttpRequest request, string entityTag)
    {
        foreach (var headerValue in request.Headers[HeaderNames.IfNoneMatch])
        {
            if (headerValue is null)
            {
                continue;
            }

            foreach (var candidate in headerValue.Split(','))
            {
                var normalized = candidate.Trim();
                if (string.Equals(normalized, "*", StringComparison.Ordinal) ||
                    string.Equals(normalized, entityTag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IResult ReadModelUnavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Organization read model unavailable");

    private static IResult InvalidOrganizationId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid organization identifier");

    private static IResult InvalidUnitId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid unit identifier");

    private static IResult InvalidPositionId() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid position identifier");

    private static IResult OrganizationNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Organization not found");

    private static IResult UnitNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Unit not found");

    private static IResult PositionNotFound() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Position not found");

    private static bool TryParseOrganizationId(
        string value,
        out OrganizationId? organizationId) =>
        TryParse(value, OrganizationId.From, out organizationId);

    private static bool TryParseUnitId(string value, out UnitId? unitId) =>
        TryParse(value, UnitId.From, out unitId);

    private static bool TryParsePositionId(string value, out PositionId? positionId) =>
        TryParse(value, PositionId.From, out positionId);

    private static bool TryParse<T>(
        string value,
        Func<string, T> parser,
        out T? parsed)
        where T : class
    {
        try
        {
            parsed = parser(value);
            return true;
        }
        catch (ArgumentException)
        {
            parsed = null;
            return false;
        }
    }
}
