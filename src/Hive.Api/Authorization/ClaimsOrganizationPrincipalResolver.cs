using System.Security.Claims;
using System.Text.Json;
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
            .Cast<OrganizationId>()
            .ToArray();
        var person = ResolvePerson(principal);
        if (person is not null &&
            person.Positions.Any(position => !organizationIds.Contains(position.OrganizationId)))
        {
            person = null;
        }

        return ValueTask.FromResult(new OrganizationPrincipal(organizationIds, person));
    }

    private static PersonPrincipal? ResolvePerson(ClaimsPrincipal principal)
    {
        var personIds = principal
            .FindAll(OrganizationAuthorizationDefaults.PersonClaimType)
            .Select(static claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (personIds.Length != 1 ||
            string.IsNullOrWhiteSpace(personIds[0]) ||
            !string.Equals(personIds[0], personIds[0].Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        var positions = new List<OccupiedPosition>();
        foreach (var claim in principal.FindAll(
                     OrganizationAuthorizationDefaults.OccupiedPositionClaimType))
        {
            if (!TryParseOccupiedPosition(claim.Value, out var position))
            {
                return null;
            }

            positions.Add(position!);
        }

        return positions.Count == 0
            ? null
            : new PersonPrincipal(personIds[0], positions);
    }

    private static bool TryParseOccupiedPosition(
        string value,
        out OccupiedPosition? position)
    {
        position = null;
        try
        {
            var claim = JsonSerializer.Deserialize<OccupiedPositionClaim>(value);
            if (claim is null)
            {
                return false;
            }

            position = new OccupiedPosition(
                OrganizationId.From(claim.OrganizationId),
                PositionId.From(claim.PositionId));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
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

    private sealed record OccupiedPositionClaim(
        string OrganizationId,
        string PositionId);
}
