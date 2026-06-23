using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;

namespace Hive.Tests;

/// <summary>
/// Tests for the uniqueness validator (US-F0-05-T05): unit, position and prompt ids are unique across
/// the document, schedule ids and subscription events are unique within each occupant, duplicates are
/// aggregated in a single pass and ordered deterministically, and the id-spaces that cannot collide in
/// the typed model (the single OrganizationId, the single occupant per position) are left untouched.
/// </summary>
public sealed class OrganizationConfigurationUniquenessValidatorTests
{
    [Fact]
    public void A_document_with_no_collisions_is_valid()
    {
        var config = BuildConfiguration(
            prompts: new[] { Prompt("ceo-v1"), Prompt("engineer-v1") },
            units: new[] { Unit("raiz", "ceo"), Unit("engenharia", "delivery-lead", parent: "raiz") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position(
                    "delivery-lead",
                    "engenharia",
                    schedule: new[] { Schedule("relatorio-diario"), Schedule("revisao-semanal") },
                    subscriptions: new[] { Subscription("directive-deadline-approaching"), Subscription("report-overdue") }),
            });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Duplicate_unit_id_is_reported_at_the_repeat_pointing_at_the_first()
    {
        var config = BuildConfiguration(
            units: new[]
            {
                Unit("raiz", "ceo"),
                Unit("engenharia", "delivery-lead", parent: "raiz"),
                Unit("engenharia", "other-lead", parent: "raiz"),
            },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-unit-id", error.Code);
        Assert.Equal("units[2].id", error.Path);
        Assert.Contains("'engenharia'", error.Message);
        Assert.Contains("units[1]", error.Message);
    }

    [Fact]
    public void Duplicate_position_id_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("ceo", "raiz"),
            });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-position-id", error.Code);
        Assert.Equal("positions[1].id", error.Path);
    }

    [Fact]
    public void Duplicate_prompt_id_is_reported()
    {
        var config = BuildConfiguration(
            prompts: new[] { Prompt("shared"), Prompt("shared") },
            units: new[] { Unit("raiz", "ceo") },
            positions: new[] { Position("ceo", "raiz") });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-prompt-id", error.Code);
        Assert.Equal("prompts[1].id", error.Path);
    }

    [Fact]
    public void Duplicate_schedule_id_within_one_occupant_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[]
            {
                Position(
                    "ceo",
                    "raiz",
                    schedule: new[] { Schedule("daily"), Schedule("daily") }),
            });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-schedule-id", error.Code);
        Assert.Equal("positions[0].occupant.schedule[1].id", error.Path);
    }

    [Fact]
    public void The_same_schedule_id_under_different_occupants_is_allowed()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[]
            {
                Position("ceo", "raiz", schedule: new[] { Schedule("daily") }),
                Position("delivery-lead", "raiz", schedule: new[] { Schedule("daily") }),
            });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Duplicate_subscription_event_within_one_occupant_is_reported()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("raiz", "ceo") },
            positions: new[]
            {
                Position(
                    "ceo",
                    "raiz",
                    subscriptions: new[] { Subscription("directive-deadline-approaching"), Subscription("directive-deadline-approaching") }),
            });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        var error = Assert.Single(result.Errors);
        Assert.Equal("duplicate-subscription-event", error.Code);
        Assert.Equal("positions[0].occupant.subscriptions[1].event", error.Path);
    }

    [Fact]
    public void All_collisions_are_aggregated_and_ordered_deterministically()
    {
        var config = BuildConfiguration(
            units: new[]
            {
                Unit("raiz", "ceo"),
                Unit("raiz", "ceo"),
            },
            positions: new[]
            {
                Position("ceo", "raiz"),
                Position("ceo", "raiz"),
            });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        Assert.False(result.IsValid);
        Assert.Collection(
            result.Errors,
            error => Assert.Equal("positions[1].id", error.Path),
            error => Assert.Equal("units[1].id", error.Path));
    }

    [Fact]
    public void Unique_ids_compare_case_sensitively()
    {
        var config = BuildConfiguration(
            units: new[] { Unit("Raiz", "ceo"), Unit("raiz", "delivery-lead") },
            positions: new[] { Position("ceo", "Raiz") });

        var result = OrganizationConfigurationUniquenessValidator.Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Null_configuration_is_internal_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => OrganizationConfigurationUniquenessValidator.Validate(null!));
    }

    private static OrganizationConfiguration BuildConfiguration(
        IReadOnlyList<UnitConfiguration> units,
        IReadOnlyList<PositionConfiguration> positions,
        IReadOnlyList<PromptConfiguration>? prompts = null) =>
        new(
            new OrganizationHeader(
                OrganizationId.From("acme-delivery"),
                UnitId.From("raiz"),
                new OwnerConfiguration(OwnerType.Human, "owner@acme.pt"),
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
        IReadOnlyList<ScheduleEntryConfiguration>? schedule = null,
        IReadOnlyList<SubscriptionConfiguration>? subscriptions = null) =>
        new(
            PositionId.From(id),
            UnitId.From(unit),
            new OccupantConfiguration(
                OccupantType.AiAgent,
                schedule: schedule,
                subscriptions: subscriptions));

    private static PromptConfiguration Prompt(string id) => new(id, $"prompts/{id}.md");

    private static ScheduleEntryConfiguration Schedule(string id) =>
        new(id, "0 55 17 * * MON-FRI", "Compilar relatorio");

    private static SubscriptionConfiguration Subscription(string @event) => new(@event, "PT4H");
}
