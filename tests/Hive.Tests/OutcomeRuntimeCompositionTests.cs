using Hive.Domain.Identity;
using Hive.Domain.Governance;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class OutcomeRuntimeCompositionTests
{
    private readonly IExecutionFactsMaterializer _materializer = new ExecutionFactsMaterializer();

    [Fact]
    public void Runtime_snapshot_materializes_objective_gates_and_positive_completion_proof()
    {
        var directive = StructuredDirective();
        var observedAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var runtime = Runtime(
            observedAt: observedAt,
            deadline: observedAt,
            hasAvailableBudget: false,
            actionGateState: OutcomeActionGateState.HumanApprovalRequired,
            approvalPending: true,
            routingState: OutcomeRoutingState.Unavailable,
            dependencyResults:
            [
                new("tool.lookup", OutcomeDependencyResultState.TransientFailure),
                new("tool.publish", OutcomeDependencyResultState.PermanentFailure),
            ],
            evidence:
            [
                new("input.source", OutcomeRequirementEvidenceState.Satisfied),
                new("criterion.saved", OutcomeRequirementEvidenceState.Satisfied),
                new("criterion.notified", OutcomeRequirementEvidenceState.Satisfied),
            ],
            triggers: [OutcomePolicyTrigger.SecurityRisk]);

        var facts = _materializer.Materialize(runtime, directive);

        Assert.True(facts.DeadlineExceeded);
        Assert.True(facts.BudgetExhausted);
        Assert.True(facts.HumanApprovalRequired);
        Assert.True(facts.ApprovalPending);
        Assert.Equal(OutcomeDependencyState.PermanentFailure, facts.DependencyState);
        Assert.Equal(OutcomeAuthorityState.NotRequired, facts.AuthorityState);
        Assert.Equal(OutcomeRoutingState.Unavailable, facts.RoutingState);
        Assert.Equal(OutcomeCompletionState.Satisfied, facts.CompletionState);
        Assert.Equal([OutcomePolicyTrigger.SecurityRisk], facts.ObservedPolicyTriggers);
    }

    [Fact]
    public void Validated_proposal_assertions_derive_only_the_two_closed_execution_facts()
    {
        var proposal = new OutcomeProposal(
            OutcomeProposedIntent.Escalation,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.SuperiorDecision,
            [OutcomeBlocker.SuperiorDecision],
            nextAction: null,
            evidenceReferences: [],
            informationGaps:
            [
                new OutcomeInformationGap(
                    "input.screenshot",
                    OutcomeInformationGapMateriality.NonMaterial,
                    materialityReason: null),
                new OutcomeInformationGap(
                    "input.environment",
                    OutcomeInformationGapMateriality.Material,
                    OutcomeInformationGapMaterialityReason.ChangesSeverity),
            ],
            authorityRequest: new OutcomeAuthorityRequest(
                "Choose the release disposition.",
                OutcomeAuthorityKind.ActionDomain,
                "delivery.release-prod",
                "This position cannot authorize production release."));

        var facts = _materializer.Materialize(
            Runtime(),
            new DirectiveExecutionContract(),
            proposal,
            new OutcomeProposalAuthorityContext(
                [AuthorityKey.From("delivery.release-prod")]));

        Assert.Equal(3, facts.ContractVersion);
        Assert.True(facts.MaterialInformationGapPresent);
        Assert.True(facts.GroundedAuthorityRequestPresent);
    }

    [Fact]
    public void Historical_projections_without_v3_assertions_remain_false_without_inference()
    {
        var historicalProposal = new OutcomeProposal(
            OutcomeProposedIntent.Escalation,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.SuperiorDecision,
            blockers: [OutcomeBlocker.SuperiorDecision],
            nextAction: null,
            evidenceReferences: []);
        var runtime = Runtime(externalInterventionRequired: true);

        var historicalFacts = _materializer.Materialize(
            runtime,
            new DirectiveExecutionContract(),
            historicalProposal);
        var legacyProjection = _materializer.Materialize(
            runtime,
            new DirectiveExecutionContract());

        Assert.False(historicalFacts.MaterialInformationGapPresent);
        Assert.False(historicalFacts.GroundedAuthorityRequestPresent);
        Assert.False(legacyProjection.MaterialInformationGapPresent);
        Assert.False(legacyProjection.GroundedAuthorityRequestPresent);
    }

    [Fact]
    public void Authority_request_without_matching_validation_context_never_becomes_grounded()
    {
        var proposal = new OutcomeProposal(
            OutcomeProposedIntent.Escalation,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.SuperiorDecision,
            blockers: [OutcomeBlocker.SuperiorDecision],
            nextAction: null,
            evidenceReferences: [],
            authorityRequest: new OutcomeAuthorityRequest(
                "Choose the release disposition.",
                OutcomeAuthorityKind.ActionDomain,
                "delivery.release-prod",
                "This position cannot authorize production release."));

        var historical = _materializer.Materialize(
            Runtime(),
            new DirectiveExecutionContract(),
            proposal);
        var wrongContext = _materializer.Materialize(
            Runtime(),
            new DirectiveExecutionContract(),
            proposal,
            new OutcomeProposalAuthorityContext(
                [AuthorityKey.From("delivery.bug-triage")]));

        Assert.False(historical.GroundedAuthorityRequestPresent);
        Assert.False(wrongContext.GroundedAuthorityRequestPresent);
    }

    [Fact]
    public void Non_material_gap_presence_and_volume_never_create_the_material_gap_fact()
    {
        var proposal = new OutcomeProposal(
            OutcomeProposedIntent.ContinueWork,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.None,
            blockers: [],
            nextAction: "Continue the authorized work.",
            evidenceReferences: [],
            informationGaps:
            [
                new OutcomeInformationGap(
                    "input.screenshot",
                    OutcomeInformationGapMateriality.NonMaterial,
                    materialityReason: null),
                new OutcomeInformationGap(
                    "input.telemetry",
                    OutcomeInformationGapMateriality.NonMaterial,
                    materialityReason: null),
            ]);

        var facts = _materializer.Materialize(
            Runtime(),
            new DirectiveExecutionContract(),
            proposal);

        Assert.False(facts.MaterialInformationGapPresent);
        Assert.False(facts.GroundedAuthorityRequestPresent);
    }

    [Fact]
    public void Structured_requirements_fail_closed_without_model_defaults()
    {
        var directive = StructuredDirective();

        var missing = _materializer.Materialize(
            Runtime(evidence:
            [
                new("criterion.saved", OutcomeRequirementEvidenceState.Satisfied),
                new("criterion.notified", OutcomeRequirementEvidenceState.Satisfied),
            ]),
            directive);
        var negative = _materializer.Materialize(
            Runtime(evidence:
            [
                new("input.source", OutcomeRequirementEvidenceState.Satisfied),
                new("criterion.saved", OutcomeRequirementEvidenceState.NotSatisfied),
                new("criterion.notified", OutcomeRequirementEvidenceState.Satisfied),
            ]),
            directive);
        var unstructured = _materializer.Materialize(Runtime(), new DirectiveExecutionContract());

        Assert.Equal(OutcomeCompletionState.Unknown, missing.CompletionState);
        Assert.Equal(OutcomeCompletionState.NotSatisfied, negative.CompletionState);
        Assert.Equal(OutcomeCompletionState.NotDeclared, unstructured.CompletionState);
        Assert.Throws<ArgumentException>(() => _materializer.Materialize(
            Runtime(evidence:
            [
                new("model.claim", OutcomeRequirementEvidenceState.Satisfied),
            ]),
            directive));
    }

    [Fact]
    public void Tool_failure_precedence_and_action_gate_mapping_are_closed()
    {
        var transient = _materializer.Materialize(
            Runtime(
                actionGateState: OutcomeActionGateState.Unknown,
                dependencyResults:
                [
                    new("tool.one", OutcomeDependencyResultState.Succeeded),
                    new("tool.two", OutcomeDependencyResultState.TransientFailure),
                ]),
            new DirectiveExecutionContract());
        var denied = _materializer.Materialize(
            Runtime(actionGateState: OutcomeActionGateState.Denied),
            new DirectiveExecutionContract());

        Assert.Equal(OutcomeDependencyState.TransientFailure, transient.DependencyState);
        Assert.Equal(OutcomeAuthorityState.Unknown, transient.AuthorityState);
        Assert.Equal(OutcomeAuthorityState.Denied, denied.AuthorityState);
    }

    [Fact]
    public void Policy_composition_is_deterministic_and_tighten_only()
    {
        var organization = new OutcomePolicyOverlay(
            maximumIterations: 6,
            maximumRetries: 2,
            verifierEnabled: false);
        var position = new OutcomePolicyOverlay(
            maximumIterations: 4,
            maximumRetries: 1);

        var first = OutcomePolicyComposer.ComposeV1(
            registryVersion: 7,
            registryFingerprint: "sha256:registry",
            organization,
            position,
            runtimeMaximumIterations: 3);
        var second = OutcomePolicyComposer.ComposeV1(
            registryVersion: 7,
            registryFingerprint: "sha256:registry",
            organization,
            position,
            runtimeMaximumIterations: 3);

        Assert.Equal("outcome-policy-v1/registry-7", first.Version);
        Assert.Equal(3, first.MaximumIterations);
        Assert.Equal(1, first.MaximumRetries);
        Assert.False(first.VerifierEnabled);
        Assert.Equal(Enum.GetValues<OutcomePolicyTrigger>(), first.EscalationTriggers);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.MaximumIterations, second.MaximumIterations);
        Assert.Equal(first.MaximumRetries, second.MaximumRetries);
        Assert.Equal(first.VerifierEnabled, second.VerifierEnabled);
        Assert.Equal(first.EscalationTriggers, second.EscalationTriggers);
        Assert.Throws<InvalidOperationException>(() => OutcomePolicyComposer.ComposeV1(
            7,
            "sha256:registry",
            new OutcomePolicyOverlay(maximumIterations: 9),
            positionOverlay: null));
        Assert.Throws<InvalidOperationException>(() => OutcomePolicyComposer.ComposeV1(
            7,
            "sha256:registry",
            organization,
            new OutcomePolicyOverlay(maximumIterations: 7)));
        Assert.Throws<InvalidOperationException>(() => OutcomePolicyComposer.ComposeV1(
            7,
            "sha256:registry",
            organization,
            new OutcomePolicyOverlay(verifierEnabled: true)));
    }

    [Fact]
    public async Task Registry_composition_serves_bug_triage_and_follow_up_coordination_without_function_branches()
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(registry)
            .ImportAsync(TwoFunctionOrganization());
        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);

        var provider = new RegistryOutcomePolicyProvider(registry);
        var composer = new OrganizationalOutcomeContextComposer(_materializer, provider);
        var runtime = Runtime();
        var directive = new DirectiveExecutionContract();
        var organizationId = OrganizationId.From("composition-fixture");

        var triage = await composer.ComposeAsync(
            organizationId,
            PositionId.From("bug-triage"),
            runtime,
            directive);
        var followUp = await composer.ComposeAsync(
            organizationId,
            PositionId.From("follow-up-coordination"),
            runtime,
            directive);

        Assert.IsType<OrganizationalOutcomeContext>(triage);
        Assert.IsType<OrganizationalOutcomeContext>(followUp);
        Assert.Equal(4, triage.Policy.MaximumIterations);
        Assert.Equal(1, triage.Policy.MaximumRetries);
        Assert.Equal(5, followUp.Policy.MaximumIterations);
        Assert.Equal(2, followUp.Policy.MaximumRetries);
        Assert.False(triage.Policy.VerifierEnabled);
        Assert.False(followUp.Policy.VerifierEnabled);
        Assert.NotEqual(triage.Policy.Fingerprint, followUp.Policy.Fingerprint);
        Assert.Equal(triage.Facts, followUp.Facts);
        Assert.Equal(triage.Directive, followUp.Directive);
    }

    [Fact]
    public void Yaml_parser_materializes_both_overlay_levels_and_validator_rejects_loosening()
    {
        const string yaml = """
            organization:
              id: policy-fixture
              root_unit: root
              outcome_policy:
                max_iterations: 6
                max_retries: 2
                verifier_enabled: false
              owner:
                type: human
                ref: owner@example.test
            units:
              - id: root
                parent: null
                leadership: agent
            positions:
              - id: agent
                unit: root
                reports_to: null
                occupant:
                  type: ai-agent
                  outcome_policy:
                    max_iterations: 4
                    max_retries: 1
                    verifier_enabled: true
            """;
        var parsed = new OrganizationConfigurationParser().Parse(yaml, "organization.yaml");

        Assert.True(parsed.IsSuccess);
        Assert.Equal(6, parsed.Configuration!.Organization.OutcomePolicy!.MaximumIterations);
        Assert.Equal(4, parsed.Configuration.Positions[0].Occupant.OutcomePolicy!.MaximumIterations);

        var validation = Hive.Domain.Organization.Configuration.Validation
            .OrganizationConfigurationStructuralValidator.Validate(parsed.Configuration);
        var error = Assert.Single(
            validation.Errors,
            item => item.Code == "outcome-policy-loosens-invariant");
        Assert.Equal("positions[0].occupant.outcome_policy", error.Path);
    }

    [Fact]
    public void Yaml_policy_overlay_rejects_unknown_fields_instead_of_ignoring_them()
    {
        const string yaml = """
            organization:
              id: policy-fixture
              root_unit: root
              outcome_policy:
                max_iterations: 6
                model: forbidden
              owner:
                type: human
                ref: owner@example.test
            """;

        var parsed = new OrganizationConfigurationParser().Parse(yaml, "organization.yaml");

        Assert.False(parsed.IsSuccess);
        Assert.Contains(
            parsed.Errors,
            error => error.FieldPath == "organization.outcome_policy.model" &&
                error.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bootstrap_registers_the_shared_domain_composition_and_registry_policy_seams()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.AddHiveBootstrap();
        using var host = builder.Build();

        Assert.IsType<ExecutionFactsMaterializer>(
            host.Services.GetRequiredService<IExecutionFactsMaterializer>());
        Assert.IsType<OrganizationalOutcomeResolver>(
            host.Services.GetRequiredService<IOrganizationalOutcomeResolver>());
        Assert.NotNull(host.Services.GetRequiredService<IOutcomePolicyProvider>());
        Assert.NotNull(host.Services.GetRequiredService<OrganizationalOutcomeContextComposer>());
    }

    [Fact]
    public void Registry_json_round_trips_policy_overlays_without_function_fields()
    {
        var overlay = new OutcomePolicyOverlay(4, 1, false);

        var json = RegistryJson.Serialize(overlay);
        var roundTrip = RegistryJson.Deserialize<OutcomePolicyOverlay>(json);

        Assert.Equal(overlay, roundTrip);
        Assert.Equal(
            "{\"maximumIterations\":4,\"maximumRetries\":1,\"verifierEnabled\":false}",
            json);
        Assert.DoesNotContain("triage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("follow-up", json, StringComparison.OrdinalIgnoreCase);
    }

    private static OutcomeRuntimeSnapshot Runtime(
        DateTimeOffset? observedAt = null,
        DateTimeOffset? deadline = null,
        bool hasAvailableBudget = true,
        OutcomeActionGateState actionGateState = OutcomeActionGateState.Authorized,
        bool approvalPending = false,
        OutcomeRoutingState routingState = OutcomeRoutingState.Available,
        bool externalInterventionRequired = false,
        IEnumerable<OutcomeDependencyResultFact>? dependencyResults = null,
        IEnumerable<OutcomeRequirementEvidence>? evidence = null,
        IEnumerable<OutcomePolicyTrigger>? triggers = null) =>
        new(
            iterationCount: 1,
            retryCount: 0,
            observedAt ?? new DateTimeOffset(2026, 7, 18, 11, 0, 0, TimeSpan.Zero),
            deadline,
            hasAvailableBudget,
            actionGateState,
            approvalPending,
            routingState,
            autonomousActionAvailable: true,
            delegationRequired: false,
            pendingActions: true,
            externalInterventionRequired,
            verifiableProgress: true,
            responsibilityRetained: true,
            dependencyResults,
            evidence,
            triggers);

    private static DirectiveExecutionContract StructuredDirective() =>
        new(
            requiredInputs:
            [
                new("input.source", "The source record is available."),
            ],
            completionCriteria:
            [
                new("criterion.saved", "The result is saved."),
                new("criterion.notified", "The recipient is notified."),
            ]);

    private static OrganizationConfiguration TwoFunctionOrganization()
    {
        var organizationId = OrganizationId.From("composition-fixture");
        var rootUnit = UnitId.From("root");
        var triage = PositionId.From("bug-triage");
        var followUp = PositionId.From("follow-up-coordination");

        return new OrganizationConfiguration(
            new OrganizationHeader(
                organizationId,
                rootUnit,
                new OwnerConfiguration(OwnerType.Human, "owner@example.test"),
                outcomePolicy: new OutcomePolicyOverlay(6, 2, false)),
            units:
            [
                new UnitConfiguration(rootUnit, triage, parent: null),
            ],
            positions:
            [
                new PositionConfiguration(
                    triage,
                    rootUnit,
                    new OccupantConfiguration(
                        OccupantType.AiAgent,
                        ai: new AiConfiguration("stub", "deterministic", maxIterations: 4),
                        outcomePolicy: new OutcomePolicyOverlay(4, 1)),
                    reportsTo: null),
                new PositionConfiguration(
                    followUp,
                    rootUnit,
                    new OccupantConfiguration(
                        OccupantType.AiAgent,
                        ai: new AiConfiguration("stub", "deterministic", maxIterations: 5),
                        outcomePolicy: new OutcomePolicyOverlay(5, 2)),
                    reportsTo: triage),
            ]);
    }
}
