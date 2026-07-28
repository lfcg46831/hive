using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class OrganizationalOutcomeContractTests
{
    [Fact]
    public void Contracts_are_explicitly_versioned_and_provider_neutral()
    {
        var facts = Facts();
        var directive = new DirectiveExecutionContract();
        var proposal = ContinueWorkProposal();
        var policy = Policy();
        var resolution = new OutcomeResolution(
            OutcomeKind.ContinueWork,
            [OutcomeResolutionReason.AutonomousActionAvailable],
            policy.Version,
            policy.Fingerprint,
            proposalOverridden: false,
            verifierInvoked: false);

        Assert.Equal(2, facts.ContractVersion);
        Assert.Equal(1, directive.ContractVersion);
        Assert.Equal(2, proposal.ContractVersion);
        Assert.Equal(1, policy.ContractVersion);
        Assert.Equal(4, resolution.ContractVersion);

        var contractTypes = new[]
        {
            typeof(ExecutionFacts),
            typeof(DirectiveExecutionContract),
            typeof(OutcomeProposal),
            typeof(OutcomePolicySnapshot),
            typeof(OutcomeResolution),
        };
        Assert.All(contractTypes, type => Assert.Equal("Hive.Domain", type.Assembly.GetName().Name));
        Assert.DoesNotContain(
            contractTypes.SelectMany(type => type.GetProperties()),
            property => ContainsForbiddenTerm(property.Name));
    }

    [Fact]
    public void Outcome_and_proposal_intents_have_stable_closed_wire_values()
    {
        Assert.Equal(
            [
                "ContinueWork",
                "Report.Progress",
                "Report.Done",
                "Escalation",
                "Directive",
                "ApprovalRequired",
                "Undetermined",
            ],
            OutcomeKindContract.WireValues);
        Assert.Equal(
            OutcomeProposedIntentContract.WireValues,
            OutcomeKindContract.WireValues[..^1]);

        foreach (var intent in Enum.GetValues<OutcomeProposedIntent>())
        {
            var wireValue = OutcomeProposedIntentContract.ToWireValue(intent);
            Assert.True(OutcomeProposedIntentContract.TryParseWireValue(wireValue, out var parsed));
            Assert.Equal(intent, parsed);
        }

        Assert.False(OutcomeProposedIntentContract.TryParseWireValue("Report", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OutcomeProposedIntentContract.ToWireValue((OutcomeProposedIntent)0));
    }

    [Fact]
    public void Execution_facts_snapshot_runtime_values_without_model_content()
    {
        var triggers = new List<OutcomePolicyTrigger>
        {
            OutcomePolicyTrigger.SecurityRisk,
        };

        var facts = Facts(triggers);
        triggers.Add(OutcomePolicyTrigger.PrivacyRisk);

        Assert.Equal(2, facts.IterationCount);
        Assert.Equal(1, facts.RetryCount);
        Assert.False(facts.DeadlineExceeded);
        Assert.False(facts.BudgetExhausted);
        Assert.Equal(OutcomeDependencyState.Available, facts.DependencyState);
        Assert.Equal(OutcomeAuthorityState.Authorized, facts.AuthorityState);
        Assert.Equal(OutcomeRoutingState.Available, facts.RoutingState);
        Assert.True(facts.AutonomousActionAvailable);
        Assert.Equal([OutcomePolicyTrigger.SecurityRisk], facts.ObservedPolicyTriggers);

        Assert.Throws<ArgumentOutOfRangeException>(() => Facts(iterationCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Facts(retryCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Facts(
            dependencyState: (OutcomeDependencyState)0));
        Assert.Throws<ArgumentException>(() => Facts(
            [OutcomePolicyTrigger.SecurityRisk, OutcomePolicyTrigger.SecurityRisk]));
    }

    [Fact]
    public void Directive_contract_exposes_only_structured_inputs_and_completion_criteria()
    {
        var inputs = new List<DirectiveExecutionRequirement>
        {
            new("input.issue", "Issue data is available."),
        };
        var criteria = new List<DirectiveExecutionRequirement>
        {
            new("criterion.assessed", "The requested assessment is complete."),
        };

        var contract = new DirectiveExecutionContract(inputs, criteria);
        inputs.Clear();
        criteria.Clear();

        Assert.Equal("input.issue", Assert.Single(contract.RequiredInputs).Reference);
        Assert.Equal("criterion.assessed", Assert.Single(contract.CompletionCriteria).Reference);
        Assert.Throws<ArgumentException>(() =>
            new DirectiveExecutionRequirement("free form evidence", "Description."));
        Assert.Throws<ArgumentException>(() => new DirectiveExecutionContract(
            [
                new DirectiveExecutionRequirement("input.same", "First."),
                new DirectiveExecutionRequirement("input.same", "Second."),
            ]));
    }

    [Fact]
    public void Outcome_proposal_enforces_positive_report_proof_and_coherent_branches()
    {
        var evidence = Evidence();

        Assert.Equal(OutcomeProposedIntent.ContinueWork, ContinueWorkProposal().ProposedIntent);
        Assert.Equal(
            OutcomeProposedIntent.ReportProgress,
            new OutcomeProposal(
                OutcomeProposedIntent.ReportProgress,
                OutcomeWorkState.InProgress,
                OutcomeRequiredIntervention.None,
                [],
                "Continue the authorized investigation.",
                evidence).ProposedIntent);
        Assert.Equal(
            OutcomeProposedIntent.ReportDone,
            new OutcomeProposal(
                OutcomeProposedIntent.ReportDone,
                OutcomeWorkState.Completed,
                OutcomeRequiredIntervention.None,
                [],
                nextAction: null,
                evidence).ProposedIntent);
        Assert.Equal(
            OutcomeProposedIntent.Escalation,
            new OutcomeProposal(
                OutcomeProposedIntent.Escalation,
                OutcomeWorkState.Blocked,
                OutcomeRequiredIntervention.SuperiorDecision,
                [OutcomeBlocker.SuperiorDecision],
                nextAction: null,
                evidence).ProposedIntent);
        Assert.Equal(
            OutcomeProposedIntent.Directive,
            new OutcomeProposal(
                OutcomeProposedIntent.Directive,
                OutcomeWorkState.InProgress,
                OutcomeRequiredIntervention.Delegation,
                [],
                "Delegate the authorized action.",
                evidence).ProposedIntent);
        Assert.Equal(
            OutcomeProposedIntent.ApprovalRequired,
            new OutcomeProposal(
                OutcomeProposedIntent.ApprovalRequired,
                OutcomeWorkState.Blocked,
                OutcomeRequiredIntervention.HumanApproval,
                [OutcomeBlocker.HumanApproval],
                nextAction: null,
                evidence).ProposedIntent);

        Assert.Throws<ArgumentException>(() => new OutcomeProposal(
            OutcomeProposedIntent.ReportProgress,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.None,
            [],
            "Continue.",
            []));
        Assert.Throws<ArgumentException>(() => new OutcomeProposal(
            OutcomeProposedIntent.ReportDone,
            OutcomeWorkState.Completed,
            OutcomeRequiredIntervention.None,
            [],
            "Do more work.",
            evidence));
        Assert.Throws<ArgumentException>(() => new OutcomeProposal(
            OutcomeProposedIntent.ApprovalRequired,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.HumanApproval,
            [OutcomeBlocker.AuthorityBoundary],
            null,
            evidence));
    }

    [Fact]
    public void Policy_and_resolution_preserve_only_versioned_closed_audit_data()
    {
        var policy = Policy();
        var resolution = new OutcomeResolution(
            OutcomeKind.Escalation,
            [
                OutcomeResolutionReason.BudgetExhausted,
                OutcomeResolutionReason.PolicyTriggerObserved,
            ],
            policy.Version,
            policy.Fingerprint,
            proposalOverridden: true,
            verifierInvoked: false);

        Assert.Equal("outcome-policy-v1", policy.Version);
        Assert.Equal("sha256:abc123", policy.Fingerprint);
        Assert.Equal(OutcomeKind.Escalation, policy.FailSafeOutcome);
        Assert.Equal(3, policy.MaximumIterations);
        Assert.Equal(2, policy.MaximumRetries);
        Assert.Equal([OutcomePolicyTrigger.SecurityRisk], policy.EscalationTriggers);
        Assert.Equal(OutcomeKind.Escalation, resolution.Outcome);
        Assert.Equal(
            [OutcomeResolutionReason.BudgetExhausted, OutcomeResolutionReason.PolicyTriggerObserved],
            resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
        Assert.False(resolution.VerifierInvoked);
        Assert.Null(resolution.VerifierStatus);
        Assert.Null(resolution.VerifierClassification);
        Assert.False(resolution.SemanticCompletionCandidate);
        Assert.Null(resolution.SemanticCompletionIneligibilityReasons);
        Assert.Equal("budget-exhausted", OutcomeResolutionReasonContract.ToWireValue(
            OutcomeResolutionReason.BudgetExhausted));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OutcomePolicySnapshot("v1", "sha256:abc", -1, 0, false));
        Assert.Throws<ArgumentException>(() => new OutcomeResolution(
            OutcomeKind.Escalation,
            [],
            "v1",
            "sha256:abc",
            false,
            false));
        Assert.Throws<ArgumentException>(() => new OutcomeResolution(
            OutcomeKind.Escalation,
            [OutcomeResolutionReason.VerifierDisagreement],
            "v1",
            "sha256:abc",
            true,
            verifierInvoked: true,
            verifierStatus: OutcomeVerifierResultStatus.Classified,
            verifierClassification: null));
        Assert.Throws<ArgumentException>(() => new OutcomeResolution(
            OutcomeKind.Escalation,
            [OutcomeResolutionReason.BudgetExhausted, OutcomeResolutionReason.BudgetExhausted],
            "v1",
            "sha256:abc",
            false,
            false));
        Assert.Throws<ArgumentException>(() => new OutcomeResolution(
            OutcomeKind.ReportDone,
            [OutcomeResolutionReason.SemanticCompletionVerified],
            "v1",
            "sha256:abc",
            false,
            false,
            semanticCompletionCandidate: true));
        Assert.Throws<ArgumentException>(() => new OutcomeResolution(
            OutcomeKind.Escalation,
            [OutcomeResolutionReason.VerifierDisagreement],
            "v1",
            "sha256:abc",
            true,
            true,
            semanticCompletionCandidate: false,
            semanticCompletionIneligibilityReasons: []));

        var evaluated = new OutcomeResolution(
            OutcomeKind.Escalation,
            [OutcomeResolutionReason.VerifierDisagreement],
            "v1",
            "sha256:abc",
            true,
            true,
            semanticCompletionCandidate: false,
            semanticCompletionIneligibilityReasons:
            [
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceReferenceNotInContext,
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceSourceNotDirectiveInput,
            ]);
        Assert.Equal(
            [
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceSourceNotDirectiveInput,
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceReferenceNotInContext,
            ],
            evaluated.SemanticCompletionIneligibilityReasons);
    }

    private static ExecutionFacts Facts(
        IEnumerable<OutcomePolicyTrigger>? triggers = null,
        int iterationCount = 2,
        int retryCount = 1,
        OutcomeDependencyState dependencyState = OutcomeDependencyState.Available) =>
        new(
            iterationCount,
            retryCount,
            deadlineExceeded: false,
            budgetExhausted: false,
            humanApprovalRequired: false,
            approvalPending: false,
            dependencyState,
            OutcomeAuthorityState.Authorized,
            OutcomeRoutingState.Available,
            autonomousActionAvailable: true,
            delegationRequired: false,
            pendingActions: true,
            externalInterventionRequired: false,
            verifiableProgress: true,
            responsibilityRetained: true,
            OutcomeCompletionState.NotSatisfied,
            triggers);

    private static OutcomeProposal ContinueWorkProposal() =>
        new(
            OutcomeProposedIntent.ContinueWork,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.None,
            [],
            "Run the next authorized check.",
            Evidence());

    private static OutcomeEvidenceReference[] Evidence() =>
        [new(OutcomeEvidenceSource.RuntimeFact, "iteration.progress")];

    private static OutcomePolicySnapshot Policy() =>
        new(
            "outcome-policy-v1",
            "sha256:abc123",
            maximumIterations: 3,
            maximumRetries: 2,
            verifierEnabled: true,
            [OutcomePolicyTrigger.SecurityRisk]);

    private static bool ContainsForbiddenTerm(string value) =>
        new[] { "provider", "model", "triage" }
            .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
