using Hive.Domain.Identity;
using Hive.Domain.Governance;
using Hive.Domain.Organization.Configuration;

namespace Hive.Tests;

/// <summary>
/// Tests for the typed, loaded configuration model (US-F0-05-T03): that it faithfully preserves the
/// §4.8 document shape with the §6.2 occupant interior, keeps the <see langword="null"/> edges of the
/// root unit and root leadership, defaults optional sections to empty rather than <see langword="null"/>,
/// rejects missing required blocks per §9.3, and — by design — leaves uniqueness (US-F0-05-T05),
/// cross-references (US-F0-05-T06) and structural rules (US-F0-05-T07) to later layers.
/// </summary>
public sealed class OrganizationConfigurationModelTests
{
    [Fact]
    public void Loaded_document_preserves_every_declared_block()
    {
        var config = BuildSampleConfiguration();

        Assert.Equal("acme-delivery", config.Organization.Id.Value);
        Assert.Equal("ACME Engenharia/Delivery", config.Organization.Name);
        Assert.Equal("raiz", config.Organization.RootUnit.Value);
        Assert.Equal(OwnerType.Human, config.Organization.Owner.Type);
        Assert.Equal("owner@acme.pt", config.Organization.Owner.Ref);

        Assert.Collection(
            config.Prompts,
            prompt =>
            {
                Assert.Equal("ceo-v1", prompt.Id);
                Assert.Equal("prompts/ceo-v1.md", prompt.Path);
            },
            prompt =>
            {
                Assert.Equal("engineer-v1", prompt.Id);
                Assert.Equal("prompts/engineer-v1.md", prompt.Path);
            });

        Assert.Equal(2, config.Units.Count);
        Assert.Equal(2, config.Positions.Count);
    }

    [Fact]
    public void Root_unit_and_root_leadership_carry_the_null_edges()
    {
        var config = BuildSampleConfiguration();

        var root = config.Units.Single(unit => unit.Id.Value == "raiz");
        Assert.Null(root.Parent);
        Assert.Equal("ACME", root.Name);
        Assert.Equal("ceo", root.Leadership.Value);

        var ceo = config.Positions.Single(position => position.Id.Value == "ceo");
        Assert.Null(ceo.ReportsTo);
        Assert.Equal("raiz", ceo.Unit.Value);
    }

    [Fact]
    public void Non_root_edges_reference_their_parent_and_superior()
    {
        var config = BuildSampleConfiguration();

        var engineering = config.Units.Single(unit => unit.Id.Value == "engenharia");
        Assert.Equal("raiz", engineering.Parent!.Value);

        var lead = config.Positions.Single(position => position.Id.Value == "delivery-lead");
        Assert.Equal("ceo", lead.ReportsTo!.Value);
        Assert.Equal("engenharia", lead.Unit.Value);
        Assert.Equal("Europe/Lisbon", lead.Timezone);
    }

    [Fact]
    public void Ai_occupant_interior_is_captured_in_full()
    {
        var config = BuildSampleConfiguration();
        var occupant = config.Positions.Single(position => position.Id.Value == "delivery-lead").Occupant;

        Assert.Equal(OccupantType.AiAgent, occupant.Type);
        Assert.Equal("engineer-v1", occupant.IdentityPromptRef);

        var ai = Assert.IsType<AiConfiguration>(occupant.Ai);
        Assert.Equal("anthropic", ai.Provider);
        Assert.Equal("claude-sonnet-4-6", ai.Model);
        Assert.Equal(0.7, ai.Temperature);
        Assert.Equal(4096, ai.MaxTokens);
        Assert.Equal(4, ai.MaxIterations);
        Assert.Equal("PT30S", ai.Timeout);
        Assert.Equal("interactive", ai.Processing);
        Assert.Collection(
            ai.Fallback,
            fallback =>
            {
                Assert.Equal("openai", fallback.Provider);
                Assert.Equal("gpt-4.1", fallback.Model);
            });

        var budget = Assert.IsType<BudgetConfiguration>(ai.Budget);
        Assert.Equal(5.00m, budget.ReactiveMaxEurPerDay);
        Assert.Equal(1.00m, budget.ProactiveMaxEurPerDay);
        Assert.Equal(6.00m, budget.TotalMaxEurPerDay);
        Assert.Equal(60, budget.MaxCallsPerHour);

        Assert.Collection(
            occupant.Schedule,
            entry =>
            {
                Assert.Equal("relatorio-diario", entry.Id);
                Assert.Equal("0 55 17 * * MON-FRI", entry.Cron);
                Assert.Equal("Compilar e enviar relatorio diario ao superior", entry.Instruction);
            });

        Assert.Collection(
            occupant.Subscriptions,
            subscription =>
            {
                Assert.Equal("directive-deadline-approaching", subscription.Event);
                Assert.Equal("PT4H", subscription.Within);
            });

        Assert.Collection(
            occupant.Tools,
            tool =>
            {
                Assert.Equal("http", tool.Connector);
                Assert.Equal(new[] { "https://api.empresa.pt/*" }, tool.Scope);
            });

        var workingHours = Assert.IsType<WorkingHoursConfiguration>(occupant.WorkingHours);
        Assert.Equal("09:00", workingHours.Start);
        Assert.Equal("18:00", workingHours.End);

        var authority = Assert.IsType<AuthorityConfiguration>(occupant.Authority);
        Assert.Equal(new[] { "delivery.bug-triage" }, authority.CanDecide.Select(key => key.Value));
        var authorityOverride = Assert.Single(authority.Overrides);
        Assert.Equal("comms.external-official", authorityOverride.Key.Value);
        Assert.Equal(ActionDomainGate.HumanApproval, authorityOverride.Gate);
        Assert.Equal("ceo", authorityOverride.Approver);
    }

    [Fact]
    public void Optional_occupant_sections_default_to_empty_not_null()
    {
        var occupant = new OccupantConfiguration(OccupantType.Human);

        Assert.Null(occupant.Ai);
        Assert.Null(occupant.Authority);
        Assert.Null(occupant.WorkingHours);
        Assert.Null(occupant.IdentityPromptRef);
        Assert.Empty(occupant.Schedule);
        Assert.Empty(occupant.Subscriptions);
        Assert.Empty(occupant.Tools);
    }

    [Fact]
    public void Optional_top_level_blocks_default_to_empty_collections()
    {
        var config = new OrganizationConfiguration(
            new OrganizationHeader(
                OrganizationId.From("acme"),
                UnitId.From("raiz"),
                new OwnerConfiguration(OwnerType.Group, "owners@acme.pt")),
            units: Array.Empty<UnitConfiguration>(),
            positions: Array.Empty<PositionConfiguration>());

        Assert.Null(config.Organization.Name);
        Assert.Empty(config.Prompts);
        Assert.Empty(config.Units);
        Assert.Empty(config.Positions);
    }

    [Fact]
    public void Required_blocks_reject_null()
    {
        var header = new OrganizationHeader(
            OrganizationId.From("acme"),
            UnitId.From("raiz"),
            new OwnerConfiguration(OwnerType.Human, "owner@acme.pt"));

        Assert.Throws<ArgumentNullException>(() => new OrganizationConfiguration(
            organization: null!,
            Array.Empty<UnitConfiguration>(),
            Array.Empty<PositionConfiguration>()));
        Assert.Throws<ArgumentNullException>(() => new OrganizationConfiguration(
            header,
            units: null!,
            Array.Empty<PositionConfiguration>()));
        Assert.Throws<ArgumentNullException>(() => new OrganizationConfiguration(
            header,
            Array.Empty<UnitConfiguration>(),
            positions: null!));
        Assert.Throws<ArgumentNullException>(() => new OwnerConfiguration(OwnerType.Human, reference: null!));
        Assert.Throws<ArgumentNullException>(() => new AiConfiguration(provider: null!, model: "m"));
    }

    [Fact]
    public void Model_does_not_enforce_uniqueness_cross_references_or_structure()
    {
        // T03 fixes the shape only; uniqueness (T05), cross-references (T06) and structural rules
        // (T07) are out of scope. A well-typed but semantically broken document must still load.
        var occupant = new OccupantConfiguration(OccupantType.AiAgent);

        var config = new OrganizationConfiguration(
            new OrganizationHeader(
                OrganizationId.From("acme"),
                UnitId.From("missing-root"),                               // root_unit absent from units
                new OwnerConfiguration(OwnerType.Human, "owner@acme.pt")),
            units:
            [
                new UnitConfiguration(UnitId.From("dup"), PositionId.From("ghost")),   // unknown leadership
                new UnitConfiguration(UnitId.From("dup"), PositionId.From("ghost")),   // duplicate unit id
            ],
            positions:
            [
                new PositionConfiguration(
                    PositionId.From("p1"),
                    UnitId.From("unknown-unit"),                           // unit not declared
                    occupant,
                    reportsTo: PositionId.From("p1")),                     // reports to itself
            ]);

        Assert.Equal("missing-root", config.Organization.RootUnit.Value);
        Assert.Equal(2, config.Units.Count);
        Assert.Equal("p1", config.Positions[0].ReportsTo!.Value);
        Assert.Equal("unknown-unit", config.Positions[0].Unit.Value);
    }

    [Fact]
    public void Records_with_equal_content_are_equal()
    {
        var first = new OwnerConfiguration(OwnerType.Human, "owner@acme.pt");
        var second = new OwnerConfiguration(OwnerType.Human, "owner@acme.pt");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new OwnerConfiguration(OwnerType.Group, "owner@acme.pt"));
    }

    private static OrganizationConfiguration BuildSampleConfiguration()
    {
        var ceo = new PositionConfiguration(
            PositionId.From("ceo"),
            UnitId.From("raiz"),
            new OccupantConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "ceo-v1",
                authority: new AuthorityConfiguration(
                    canDecide: ["org.quarterly-priorities"])),
            reportsTo: null,
            name: "CEO");

        var deliveryLead = new PositionConfiguration(
            PositionId.From("delivery-lead"),
            UnitId.From("engenharia"),
            new OccupantConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "engineer-v1",
                ai: new AiConfiguration(
                    provider: "anthropic",
                    model: "claude-sonnet-4-6",
                    temperature: 0.7,
                    maxTokens: 4096,
                    maxIterations: 4,
                    timeout: "PT30S",
                    processing: "interactive",
                    fallback: [new AiFallbackConfiguration("openai", "gpt-4.1")],
                    budget: new BudgetConfiguration(
                        reactiveMaxEurPerDay: 5.00m,
                        proactiveMaxEurPerDay: 1.00m,
                        totalMaxEurPerDay: 6.00m,
                        maxCallsPerHour: 60)),
                workingHours: new WorkingHoursConfiguration("09:00", "18:00"),
                authority: new AuthorityConfiguration(
                    canDecide: ["delivery.bug-triage"],
                    overrides:
                    [
                        new AuthorityOverrideConfiguration(
                            "comms.external-official",
                            ActionDomainGate.HumanApproval,
                            approver: "ceo"),
                    ]),
                schedule: [new ScheduleEntryConfiguration(
                    "relatorio-diario",
                    "0 55 17 * * MON-FRI",
                    "Compilar e enviar relatorio diario ao superior")],
                subscriptions: [new SubscriptionConfiguration("directive-deadline-approaching", "PT4H")],
                tools: [new ToolConfiguration("http", ["https://api.empresa.pt/*"])]),
            reportsTo: PositionId.From("ceo"),
            name: "Delivery Lead",
            timezone: "Europe/Lisbon");

        return new OrganizationConfiguration(
            new OrganizationHeader(
                OrganizationId.From("acme-delivery"),
                UnitId.From("raiz"),
                new OwnerConfiguration(OwnerType.Human, "owner@acme.pt"),
                name: "ACME Engenharia/Delivery"),
            units:
            [
                new UnitConfiguration(UnitId.From("raiz"), PositionId.From("ceo"), parent: null, name: "ACME"),
                new UnitConfiguration(
                    UnitId.From("engenharia"),
                    PositionId.From("delivery-lead"),
                    parent: UnitId.From("raiz"),
                    name: "Engenharia"),
            ],
            positions: [ceo, deliveryLead],
            prompts:
            [
                new PromptConfiguration("ceo-v1", "prompts/ceo-v1.md"),
                new PromptConfiguration("engineer-v1", "prompts/engineer-v1.md"),
            ]);
    }
}
