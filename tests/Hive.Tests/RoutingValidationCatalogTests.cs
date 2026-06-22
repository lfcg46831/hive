using System.Reflection;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RoutingValidationCatalogTests
{
    public static TheoryData<ValidationError, string, string, RejectionReason> Entries => new()
    {
        { RoutingValidationCatalog.EndpointNotAllowed("from"), "endpoint-not-allowed", "from", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.EndpointNotAllowed("to"), "endpoint-not-allowed", "to", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.OrganizationNotFound(), "organization-not-found", "organizationId", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.PositionNotFound("from.positionId"), "position-not-found", "from.positionId", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.PositionNotFound("to.positionId"), "position-not-found", "to.positionId", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.DirectSubordinateRequired(), "direct-subordinate-required", "to.positionId", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.DirectSuperiorRequired(), "direct-superior-required", "to.positionId", RejectionReason.InvalidRoute },
        { RoutingValidationCatalog.RootLeadershipRequired(), "root-leadership-required", "from.positionId", RejectionReason.InvalidRoute },
    };

    [Theory]
    [MemberData(nameof(Entries))]
    public void Catalog_entry_has_canonical_code_path_and_reason(
        ValidationError error,
        string code,
        string path,
        RejectionReason reason)
    {
        Assert.Equal(new ValidationError(code, path, reason), error);
    }

    [Fact]
    public void Missing_organization_and_positions_map_to_invalid_route()
    {
        Assert.Equal(RejectionReason.InvalidRoute, RoutingValidationCatalog.OrganizationNotFound().Reason);
        Assert.Equal(RejectionReason.InvalidRoute, RoutingValidationCatalog.PositionNotFound("from.positionId").Reason);
        Assert.Equal(RejectionReason.InvalidRoute, RoutingValidationCatalog.PositionNotFound("to.positionId").Reason);
    }

    [Fact]
    public void Catalog_codes_are_unique()
    {
        var codes = typeof(RoutingValidationCatalog.Codes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false })
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalog_shares_the_canonical_not_found_contract_with_governance()
    {
        Assert.Equal(
            ApprovalValidationCatalog.OrganizationNotFound(),
            RoutingValidationCatalog.OrganizationNotFound());
    }
}
