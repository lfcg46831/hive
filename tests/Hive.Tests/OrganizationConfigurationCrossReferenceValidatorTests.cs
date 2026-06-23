using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;

namespace Hive.Tests;

/// <summary>
/// Tests for the cross-reference validator (US-F0-05-T06): the §4.8 resolvable reference edges —
/// <c>organization.root_unit</c>, <c>units[].leadership</c>, <c>positions[].unit</c>,
/// <c>positions[].reports_to</c> and <c>occupant.identity_prompt_ref</c> — each resolve to a declared
/// entity, the <c>OrganizationOwner</c> is present, unresolved references are aggregated in a single
/// pass and ordered deterministically, and resolution is independent of the uniqueness rules.
/// </summary>
public sealed class OrganizationConfigurationCrossReferenceValidatorTests
{
    [Fact]
    public void A_document_whose_references_all_resolve_is_valid()
    {
        var config = BuildConfiguration(
            prompts: new[] { Prompt("ceo-v1"), Prompt("engineer-v1") },
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "delivery-lead", parent: "raiz") },
            positions: new[]
            {
                Position("ceo", "raiz", promptRef: "ceo-v1"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo", promptRef: "engineer-v1"),
            });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_root_unit_absent_from_units_is_reported()
    {
        var config = BuildConfiguration(
            rootUnit: "ghost-root",
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("root-unit-not-found", error.Code);
        Assert.Equal("organization.root_unit", error.Path);
        Assert.Contains("'ghost-root'", error.Message);
    }

    [Fact]
    public void A_unit_leadership_with_no_matching_position_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "ghost-lead", parent: "raiz") },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("leadership-position-not-found", error.Code);
        Assert.Equal("units[1].leadership", error.Path);
        Assert.Contains("'ghost-lead'", error.Message);
    }

    [Fact]
    public void A_position_in_a_unit_that_does_not_exist_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "ghost-unit") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("unit-not-found", error.Code);
        Assert.Equal("positions[0].unit", error.Path);
        Assert.Contains("'ghost-unit'", error.Message);
    }

    [Fact]
    public void A_reports_to_with_no_matching_position_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "raiz", reportsTo: "ghost-superior"),
            });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("reports-to-position-not-found", error.Code);
        Assert.Equal("positions[1].reports_to", error.Path);
        Assert.Contains("'ghost-superior'", error.Message);
    }

    [Fact]
    public void A_null_reports_to_is_not_a_broken_reference()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "raiz", reportsTo: null) });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void An_identity_prompt_ref_absent_from_the_catalog_is_reported()
    {
        var config = BuildConfiguration(
            prompts: new[] { Prompt("ceo-v1") },
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "raiz", promptRef: "ghost-prompt") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("identity-prompt-not-found", error.Code);
        Assert.Equal("positions[0].occupant.identity_prompt_ref", error.Path);
        Assert.Contains("'ghost-prompt'", error.Message);
    }

    [Fact]
    public void An_absent_identity_prompt_ref_is_not_a_broken_reference()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "raiz", promptRef: null) });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_blank_organization_owner_reference_is_reported()
    {
        var config = BuildConfiguration(
            owner: new OwnerConfiguration(OwnerType.Human, "   "),
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("organization-owner-required", error.Code);
        Assert.Equal("organization.owner.ref", error.Path);
    }

    [Fact]
    public void References_resolve_even_when_the_target_id_is_duplicated()
    {
        // A duplicate position id is a uniqueness problem (US-F0-05-T05), not a cross-reference one:
        // the reference still resolves because the target is declared.
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("ceo", "raiz"),
            });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void References_compare_case_sensitively()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "Raiz") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("unit-not-found", error.Code);
        Assert.Equal("positions[0].unit", error.Path);
    }

    [Fact]
    public void All_unresolved_references_are_aggregated_and_ordered_deterministically()
    {
        var config = BuildConfiguration(
            rootUnit: "ghost-root",
            units: new[] { Unit("raiz", "ghost-lead") },
            positions: new[] { Position("ceo", "ghost-unit") });

        var result = OrganizationConfigurationCrossReferenceValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Collection(
            result.Errors,
            error => Assert.Equal("organization.root_unit", error.Path),
            error => Assert.Equal("positions[0].unit", error.Path),
            error => Assert.Equal("units[0].leadership", error.Path));
    }

    [Fact]
    public void Null_configuration_is_internal_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => OrganizationConfigurationCrossReferenceValidator.Validate(null!));
    }

    private static OrganizationConfiguration BuildConfiguration(
        IReadOnlyList<UnitConfiguration> units,
        IReadOnlyList<PositionConfiguration> positions,
        IReadOnlyList<PromptConfiguration>? prompts = null,
        string rootUnit = "raiz",
        OwnerConfiguration? owner = null) =>
        new(
            new OrganizationHeader(
                OrganizationId.From("acme-delivery"),
                UnitId.From(rootUnit),
                owner ?? new OwnerConfiguration(OwnerType.Human, "owner@acme.pt"),
                name: "ACME"),
            units,
            positions,
            prompts);

    private static UnitConfiguration Unit(string id, string leadership, string? parent = null) =>
        new(
            UnitId.From(id),
            PositionId.From(leadership),
            parent is null ? null : UnitId.From(parent));

    private static PositionConfiguration Position(
        string id,
        string unit,
        string? reportsTo = null,
        string? promptRef = null) =>
        new(
            PositionId.From(id),
            UnitId.From(unit),
            new OccupantConfiguration(OccupantType.AiAgent, identityPromptRef: promptRef),
            reportsTo is null ? null : PositionId.From(reportsTo));

    private static PromptConfiguration Prompt(string id) => new(id, $"prompts/{id}.md");
}
