using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;

namespace Hive.Tests;

/// <summary>
/// Tests for the structural validator (US-F0-05-T07): each unit has exactly one leadership (a position
/// that belongs to it, and no position leads two units), the <c>units[].parent</c> edges form a single
/// tree rooted at <c>organization.root_unit</c> with no cycles, violations are aggregated in a single
/// pass and ordered deterministically, and the rules are independent of uniqueness (US-F0-05-T05) and
/// cross-references (US-F0-05-T06).
/// </summary>
public sealed class OrganizationConfigurationStructuralValidatorTests
{
    [Fact]
    public void A_structurally_sound_document_is_valid()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "delivery-lead", parent: "raiz") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_leadership_position_belonging_to_another_unit_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "ceo", parent: "raiz") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        // 'ceo' belongs to 'raiz' but leads 'engenharia' (and is reused as leadership of two units).
        Assert.Contains(
            result.Errors,
            error => error is { Code: "leadership-not-in-unit", Path: "units[1].leadership" });
        Assert.Contains("'ceo'", Message(result, "leadership-not-in-unit"));
    }

    [Fact]
    public void A_position_leading_more_than_one_unit_is_reported()
    {
        var config = BuildConfiguration(
            units: new[]
            {
                Unit("raiz", "ceo"),
                Unit("engenharia", "ceo", parent: "raiz"),
            },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        var error = Assert.Single(
            result.Errors,
            e => e.Code == "position-leads-multiple-units");
        Assert.Equal("units[1].leadership", error.Path);
        Assert.Contains("'raiz'", error.Message);
    }

    [Fact]
    public void A_leadership_that_does_not_resolve_to_any_position_is_left_to_cross_references()
    {
        // 'ghost-lead' exists in no position: that is a cross-reference problem (US-F0-05-T06),
        // so the structural validator stays silent rather than masking it.
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ghost-lead") },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.DoesNotContain(result.Errors, error => error.Code == "leadership-not-in-unit");
    }

    [Fact]
    public void A_parent_unit_that_does_not_resolve_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "delivery-lead", parent: "ghost-unit") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        var error = Assert.Single(result.Errors, e => e.Code == "parent-unit-not-found");
        Assert.Equal("units[1].parent", error.Path);
        Assert.Contains("'ghost-unit'", error.Message);
    }

    [Fact]
    public void The_root_unit_declaring_a_parent_is_reported()
    {
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[] { Unit("raiz", "ceo", parent: "raiz") },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.Contains(
            result.Errors,
            error => error is { Code: "root-unit-parent-not-null", Path: "units[0].parent" });
    }

    [Fact]
    public void A_non_root_unit_without_a_parent_is_reported()
    {
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "delivery-lead") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        var error = Assert.Single(result.Errors, e => e.Code == "non-root-unit-without-parent");
        Assert.Equal("units[1].parent", error.Path);
        Assert.Contains("'engenharia'", error.Message);
    }

    [Fact]
    public void A_two_unit_parent_cycle_is_reported_for_each_unit_on_it()
    {
        // 'a' and 'b' point at each other: neither reaches the rooted tree, so both are on the cycle.
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[]
            {
                Unit("raiz", "ceo"),
                Unit("a", "lead-a", parent: "b"),
                Unit("b", "lead-b", parent: "a"),
            },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("lead-a", "a", reportsTo: "ceo"),
                Position("lead-b", "b", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        var cycleErrors = result.Errors.Where(e => e.Code == "unit-parent-cycle").ToArray();
        Assert.Equal(2, cycleErrors.Length);
        Assert.Collection(
            cycleErrors,
            error => Assert.Equal("units[1].parent", error.Path),
            error => Assert.Equal("units[2].parent", error.Path));
    }

    [Fact]
    public void A_self_parenting_unit_is_a_cycle()
    {
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[] { Unit("raiz", "ceo"), Unit("a", "lead-a", parent: "a") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("lead-a", "a", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.Contains(
            result.Errors,
            error => error is { Code: "unit-parent-cycle", Path: "units[1].parent" });
    }

    [Fact]
    public void A_unit_descending_from_a_cycle_is_not_itself_reported_as_a_cycle()
    {
        // 'c' descends from the a<->b cycle but is not on it: only 'a' and 'b' are reported.
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[]
            {
                Unit("raiz", "ceo"),
                Unit("a", "lead-a", parent: "b"),
                Unit("b", "lead-b", parent: "a"),
                Unit("c", "lead-c", parent: "a"),
            },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("lead-a", "a", reportsTo: "ceo"),
                Position("lead-b", "b", reportsTo: "ceo"),
                Position("lead-c", "c", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        var cyclePaths = result.Errors
            .Where(e => e.Code == "unit-parent-cycle")
            .Select(e => e.Path)
            .ToArray();
        Assert.Equal(new[] { "units[1].parent", "units[2].parent" }, cyclePaths);
    }

    [Fact]
    public void Unit_ids_compare_case_sensitively()
    {
        // 'Raiz' (parent) is not the declared 'raiz', so the parent does not resolve.
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "delivery-lead", parent: "Raiz") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.Contains(
            result.Errors,
            error => error is { Code: "parent-unit-not-found", Path: "units[1].parent" });
    }

    [Fact]
    public void Structural_rules_hold_even_when_a_unit_id_is_duplicated()
    {
        // A duplicate unit id is a uniqueness problem (US-F0-05-T05); the parent still resolves and the
        // structure is otherwise sound, so the structural validator reports nothing.
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[]
            {
                Unit("raiz", "ceo"),
                Unit("engenharia", "delivery-lead", parent: "raiz"),
                Unit("engenharia", "qa-lead", parent: "raiz"),
            },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
                Position("qa-lead", "engenharia", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void All_structural_violations_are_aggregated_and_ordered_deterministically()
    {
        var config = BuildConfiguration(
            rootUnit: "raiz",
            units: new[]
            {
                Unit("raiz", "ceo", parent: "raiz"),               // root-unit-parent-not-null + cycle
                Unit("engenharia", "delivery-lead"),               // non-root-unit-without-parent
                Unit("vendas", "sales-lead", parent: "ghost"),     // parent-unit-not-found
            },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("delivery-lead", "engenharia", reportsTo: "ceo"),
                Position("sales-lead", "vendas", reportsTo: "ceo"),
            });

        var result = OrganizationConfigurationStructuralValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Collection(
            result.Errors,
            error =>
            {
                Assert.Equal("units[0].parent", error.Path);
                Assert.Equal("root-unit-parent-not-null", error.Code);
            },
            error =>
            {
                Assert.Equal("units[0].parent", error.Path);
                Assert.Equal("unit-parent-cycle", error.Code);
            },
            error =>
            {
                Assert.Equal("units[1].parent", error.Path);
                Assert.Equal("non-root-unit-without-parent", error.Code);
            },
            error =>
            {
                Assert.Equal("units[2].parent", error.Path);
                Assert.Equal("parent-unit-not-found", error.Code);
            });
    }

    [Fact]
    public void Null_configuration_is_internal_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => OrganizationConfigurationStructuralValidator.Validate(null!));
    }

    private static string Message(OrganizationConfigurationValidationResult result, string code) =>
        result.Errors.First(error => error.Code == code).Message;

    private static OrganizationConfiguration BuildConfiguration(
        IReadOnlyList<UnitConfiguration> units,
        IReadOnlyList<PositionConfiguration> positions,
        string rootUnit = "raiz",
        OwnerConfiguration? owner = null) =>
        new(
            new OrganizationHeader(
                OrganizationId.From("acme-delivery"),
                UnitId.From(rootUnit),
                owner ?? new OwnerConfiguration(OwnerType.Human, "owner@acme.pt"),
                name: "ACME"),
            units,
            positions);

    private static UnitConfiguration Unit(string id, string leadership, string? parent = null) =>
        new(
            UnitId.From(id),
            PositionId.From(leadership),
            parent is null ? null : UnitId.From(parent));

    private static PositionConfiguration Position(string id, string unit, string? reportsTo = null) =>
        new(
            PositionId.From(id),
            UnitId.From(unit),
            new OccupantConfiguration(OccupantType.AiAgent),
            reportsTo is null ? null : PositionId.From(reportsTo));
}
