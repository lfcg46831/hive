using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class OrganizationalOutcomeResolverTests
{
    private readonly IOrganizationalOutcomeResolver _resolver = new OrganizationalOutcomeResolver();

    [Fact]
    public void Resolver_is_a_synchronous_provider_neutral_domain_service()
    {
        var method = typeof(IOrganizationalOutcomeResolver).GetMethod(
            nameof(IOrganizationalOutcomeResolver.Resolve));

        Assert.NotNull(method);
        Assert.Equal(typeof(OutcomeResolution), method.ReturnType);
        Assert.Equal(
            [
                typeof(ExecutionFacts),
                typeof(DirectiveExecutionContract),
                typeof(OutcomeProposal),
                typeof(OutcomePolicySnapshot),
            ],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Empty(typeof(OrganizationalOutcomeResolver).GetConstructors().Single().GetParameters());
        Assert.Equal("Hive.Domain", typeof(OrganizationalOutcomeResolver).Assembly.GetName().Name);
    }

    [Fact]
    public void Human_approval_has_precedence_over_every_other_branch()
    {
        var facts = Facts(
            deadlineExceeded: true,
            budgetExhausted: true,
            humanApprovalRequired: true,
            approvalPending: true,
            dependencyState: OutcomeDependencyState.PermanentFailure,
            authorityState: OutcomeAuthorityState.Denied,
            routingState: OutcomeRoutingState.Unavailable,
            triggers: [OutcomePolicyTrigger.SecurityRisk]);

        var resolution = Resolve(facts, EscalationProposal());

        Assert.Equal(OutcomeKind.ApprovalRequired, resolution.Outcome);
        Assert.Equal(
            [OutcomeResolutionReason.HumanApprovalGate, OutcomeResolutionReason.ApprovalPending],
            resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
        Assert.False(resolution.VerifierInvoked);
    }

    [Theory]
    [InlineData(ObjectiveGate.Deadline, OutcomeResolutionReason.DeadlineExceeded)]
    [InlineData(ObjectiveGate.Budget, OutcomeResolutionReason.BudgetExhausted)]
    [InlineData(ObjectiveGate.IterationLimit, OutcomeResolutionReason.IterationLimitReached)]
    [InlineData(ObjectiveGate.RetryLimit, OutcomeResolutionReason.RetryLimitReached)]
    [InlineData(ObjectiveGate.PermanentDependency, OutcomeResolutionReason.PermanentDependencyFailure)]
    [InlineData(ObjectiveGate.Authority, OutcomeResolutionReason.AuthorityDenied)]
    [InlineData(ObjectiveGate.Routing, OutcomeResolutionReason.RoutingUnavailable)]
    [InlineData(ObjectiveGate.PolicyTrigger, OutcomeResolutionReason.PolicyTriggerObserved)]
    public void Each_objective_gate_forces_escalation(
        ObjectiveGate gate,
        OutcomeResolutionReason expectedReason)
    {
        var facts = gate switch
        {
            ObjectiveGate.Deadline => Facts(deadlineExceeded: true),
            ObjectiveGate.Budget => Facts(budgetExhausted: true),
            ObjectiveGate.IterationLimit => Facts(iterationCount: 3),
            ObjectiveGate.RetryLimit => Facts(retryCount: 2),
            ObjectiveGate.PermanentDependency =>
                Facts(dependencyState: OutcomeDependencyState.PermanentFailure),
            ObjectiveGate.Authority => Facts(authorityState: OutcomeAuthorityState.Denied),
            ObjectiveGate.Routing => Facts(routingState: OutcomeRoutingState.Unavailable),
            ObjectiveGate.PolicyTrigger => Facts(triggers: [OutcomePolicyTrigger.SecurityRisk]),
            _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, null),
        };

        var resolution = Resolve(facts, ContinueProposal());

        Assert.Equal(OutcomeKind.Escalation, resolution.Outcome);
        Assert.Equal([expectedReason], resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
    }

    [Fact]
    public void Objective_reasons_are_complete_and_canonical_regardless_of_trigger_order()
    {
        var firstFacts = Facts(
            iterationCount: 3,
            retryCount: 2,
            deadlineExceeded: true,
            budgetExhausted: true,
            dependencyState: OutcomeDependencyState.PermanentFailure,
            authorityState: OutcomeAuthorityState.Denied,
            routingState: OutcomeRoutingState.Unavailable,
            triggers:
            [
                OutcomePolicyTrigger.PrivacyRisk,
                OutcomePolicyTrigger.SecurityRisk,
            ]);
        var secondFacts = Facts(
            iterationCount: 3,
            retryCount: 2,
            deadlineExceeded: true,
            budgetExhausted: true,
            dependencyState: OutcomeDependencyState.PermanentFailure,
            authorityState: OutcomeAuthorityState.Denied,
            routingState: OutcomeRoutingState.Unavailable,
            triggers:
            [
                OutcomePolicyTrigger.SecurityRisk,
                OutcomePolicyTrigger.PrivacyRisk,
            ]);
        var firstPolicy = Policy(
            triggers:
            [
                OutcomePolicyTrigger.SecurityRisk,
                OutcomePolicyTrigger.PrivacyRisk,
            ]);
        var secondPolicy = Policy(
            triggers:
            [
                OutcomePolicyTrigger.PrivacyRisk,
                OutcomePolicyTrigger.SecurityRisk,
            ]);

        var first = Resolve(firstFacts, ContinueProposal(), firstPolicy);
        var second = Resolve(secondFacts, ContinueProposal(), secondPolicy);

        var expected = new[]
        {
            OutcomeResolutionReason.DeadlineExceeded,
            OutcomeResolutionReason.BudgetExhausted,
            OutcomeResolutionReason.IterationLimitReached,
            OutcomeResolutionReason.RetryLimitReached,
            OutcomeResolutionReason.PermanentDependencyFailure,
            OutcomeResolutionReason.AuthorityDenied,
            OutcomeResolutionReason.RoutingUnavailable,
            OutcomeResolutionReason.PolicyTriggerObserved,
        };
        Assert.Equal(expected, first.Reasons);
        Assert.Equal(expected, second.Reasons);
    }

    [Fact]
    public void Policy_escalates_only_triggers_selected_by_the_snapshot()
    {
        var resolution = Resolve(
            Facts(triggers: [OutcomePolicyTrigger.PrivacyRisk]),
            ContinueProposal(),
            Policy(triggers: [OutcomePolicyTrigger.SecurityRisk]));

        Assert.Equal(OutcomeKind.ContinueWork, resolution.Outcome);
        Assert.Equal([OutcomeResolutionReason.AutonomousActionAvailable], resolution.Reasons);
    }

    [Fact]
    public void Proposal_can_tighten_to_escalation_but_cannot_relax_an_objective_gate()
    {
        var tightened = Resolve(Facts(), EscalationProposal());
        var gateWins = Resolve(Facts(budgetExhausted: true), ReportDoneProposal());

        Assert.Equal(OutcomeKind.Escalation, tightened.Outcome);
        Assert.Equal([OutcomeResolutionReason.ProposalEscalation], tightened.Reasons);
        Assert.False(tightened.ProposalOverridden);
        Assert.Equal(
            "proposal-escalation",
            OutcomeResolutionReasonContract.ToWireValue(
                OutcomeResolutionReason.ProposalEscalation));

        Assert.Equal(OutcomeKind.Escalation, gateWins.Outcome);
        Assert.Equal([OutcomeResolutionReason.BudgetExhausted], gateWins.Reasons);
        Assert.True(gateWins.ProposalOverridden);
    }

    [Fact]
    public void Autonomous_work_precedes_delegation_and_message_materialization()
    {
        var facts = Facts(
            autonomousActionAvailable: true,
            delegationRequired: true,
            pendingActions: true,
            verifiableProgress: true,
            completionState: OutcomeCompletionState.Satisfied);

        var resolution = Resolve(facts, ReportDoneProposal());

        Assert.Equal(OutcomeKind.ContinueWork, resolution.Outcome);
        Assert.Equal([OutcomeResolutionReason.AutonomousActionAvailable], resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
    }

    [Fact]
    public void Authorized_delegation_precedes_reports_when_no_autonomous_step_exists()
    {
        var facts = Facts(
            autonomousActionAvailable: false,
            delegationRequired: true,
            pendingActions: true,
            verifiableProgress: true,
            completionState: OutcomeCompletionState.Satisfied);

        var resolution = Resolve(facts, ReportDoneProposal());

        Assert.Equal(OutcomeKind.Directive, resolution.Outcome);
        Assert.Equal([OutcomeResolutionReason.DelegationRequired], resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
    }

    [Theory]
    [InlineData(OutcomeRequiredIntervention.None, false)]
    [InlineData(OutcomeRequiredIntervention.HumanApproval, true)]
    [InlineData(OutcomeRequiredIntervention.SuperiorDecision, true)]
    [InlineData(OutcomeRequiredIntervention.ExternalAction, true)]
    [InlineData(OutcomeRequiredIntervention.Delegation, false)]
    public void Closed_intervention_classification_controls_continue_delegate_and_progress(
        OutcomeRequiredIntervention intervention,
        bool expectedExternalIntervention)
    {
        var externalIntervention =
            OutcomeRequiredInterventionContract.RequiresExternalIntervention(intervention);

        var continuation = Resolve(
            Facts(externalInterventionRequired: externalIntervention),
            ContinueProposal());
        var delegation = Resolve(
            Facts(
                autonomousActionAvailable: false,
                delegationRequired: true,
                externalInterventionRequired: externalIntervention),
            DirectiveProposal());
        var progress = Resolve(
            ProgressFacts(externalInterventionRequired: externalIntervention),
            ReportProgressProposal());

        Assert.Equal(expectedExternalIntervention, externalIntervention);
        Assert.Equal(
            expectedExternalIntervention ? OutcomeKind.Undetermined : OutcomeKind.ContinueWork,
            continuation.Outcome);
        Assert.Equal(
            expectedExternalIntervention ? OutcomeKind.Undetermined : OutcomeKind.Directive,
            delegation.Outcome);
        Assert.Equal(
            expectedExternalIntervention ? OutcomeKind.Undetermined : OutcomeKind.ReportProgress,
            progress.Outcome);
    }

    [Fact]
    public void Report_done_requires_positive_completion_proof_and_no_pending_work()
    {
        var completed = Resolve(
            Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                responsibilityRetained: false,
                completionState: OutcomeCompletionState.Satisfied),
            ReportDoneProposal());
        var incomplete = Resolve(
            Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: OutcomeCompletionState.NotSatisfied),
            ReportDoneProposal());
        var stillPending = Resolve(
            Facts(
                autonomousActionAvailable: false,
                pendingActions: true,
                completionState: OutcomeCompletionState.Satisfied),
            ReportDoneProposal());
        var unknown = Resolve(
            Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: OutcomeCompletionState.Unknown),
            ReportDoneProposal());

        Assert.Equal(OutcomeKind.ReportDone, completed.Outcome);
        Assert.Equal([OutcomeResolutionReason.CompletionCriteriaSatisfied], completed.Reasons);
        Assert.False(completed.ProposalOverridden);
        AssertUndetermined(incomplete, OutcomeResolutionReason.ContradictoryFacts);
        AssertUndetermined(stillPending, OutcomeResolutionReason.ContradictoryFacts);
        AssertUndetermined(unknown, OutcomeResolutionReason.InsufficientFacts);
    }

    [Fact]
    public void Report_progress_requires_verified_progress_retained_responsibility_and_safe_next_step()
    {
        var progress = Resolve(ProgressFacts(), ReportProgressProposal());
        var unverified = Resolve(
            ProgressFacts(verifiableProgress: false),
            ReportProgressProposal());
        var external = Resolve(
            ProgressFacts(externalInterventionRequired: true),
            ReportProgressProposal());
        var authorityUnknown = Resolve(
            ProgressFacts(authorityState: OutcomeAuthorityState.Unknown),
            ReportProgressProposal());

        Assert.Equal(OutcomeKind.ReportProgress, progress.Outcome);
        Assert.Equal([OutcomeResolutionReason.VerifiableProgress], progress.Reasons);
        Assert.False(progress.ProposalOverridden);
        Assert.Equal(OutcomeKind.ContinueWork, unverified.Outcome);
        Assert.Equal([OutcomeResolutionReason.AutonomousActionAvailable], unverified.Reasons);
        Assert.True(unverified.ProposalOverridden);
        AssertUndetermined(external, OutcomeResolutionReason.ContradictoryFacts);
        AssertUndetermined(authorityUnknown, OutcomeResolutionReason.InsufficientFacts);
    }

    [Fact]
    public void Missing_or_conflicting_proof_is_undetermined_and_never_defaults_to_report()
    {
        var missingCompletion = Resolve(
            Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: OutcomeCompletionState.NotDeclared),
            ReportDoneProposal(),
            directive: Directive());
        var unconfirmedApproval = Resolve(
            Facts(autonomousActionAvailable: false, pendingActions: false),
            ApprovalProposal());
        var unsupportedContinue = Resolve(
            Facts(autonomousActionAvailable: false, pendingActions: false),
            ContinueProposal());

        AssertUndetermined(missingCompletion, OutcomeResolutionReason.InsufficientFacts);
        AssertUndetermined(unconfirmedApproval, OutcomeResolutionReason.ContradictoryFacts);
        AssertUndetermined(unsupportedContinue, OutcomeResolutionReason.ContradictoryFacts);
    }

    [Fact]
    public void Resolution_carries_the_exact_policy_identity_and_never_marks_verifier_invoked()
    {
        var policy = new OutcomePolicySnapshot(
            "policy-2026-07-17",
            "sha256:resolver-test",
            maximumIterations: 4,
            maximumRetries: 3,
            verifierEnabled: true);

        var resolution = Resolve(Facts(), ContinueProposal(), policy);

        Assert.Equal(policy.Version, resolution.PolicyVersion);
        Assert.Equal(policy.Fingerprint, resolution.PolicyFingerprint);
        Assert.False(resolution.VerifierInvoked);
    }

    [Fact]
    public void Null_contract_inputs_are_rejected_before_resolution()
    {
        var facts = Facts();
        var directive = Directive();
        var proposal = ContinueProposal();
        var policy = Policy();

        Assert.Throws<ArgumentNullException>(() => _resolver.Resolve(null!, directive, proposal, policy));
        Assert.Throws<ArgumentNullException>(() => _resolver.Resolve(facts, null!, proposal, policy));
        Assert.Throws<ArgumentNullException>(() => _resolver.Resolve(facts, directive, null!, policy));
        Assert.Throws<ArgumentNullException>(() => _resolver.Resolve(facts, directive, proposal, null!));
    }

    private OutcomeResolution Resolve(
        ExecutionFacts facts,
        OutcomeProposal proposal,
        OutcomePolicySnapshot? policy = null,
        DirectiveExecutionContract? directive = null) =>
        _resolver.Resolve(facts, directive ?? Directive(), proposal, policy ?? Policy());

    private static void AssertUndetermined(
        OutcomeResolution resolution,
        OutcomeResolutionReason reason)
    {
        Assert.Equal(OutcomeKind.Undetermined, resolution.Outcome);
        Assert.Equal([reason], resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
        Assert.False(resolution.VerifierInvoked);
    }

    private static ExecutionFacts ProgressFacts(
        bool verifiableProgress = true,
        bool externalInterventionRequired = false,
        OutcomeAuthorityState authorityState = OutcomeAuthorityState.Authorized) =>
        Facts(
            autonomousActionAvailable: true,
            pendingActions: true,
            externalInterventionRequired: externalInterventionRequired,
            verifiableProgress: verifiableProgress,
            responsibilityRetained: true,
            authorityState: authorityState,
            completionState: OutcomeCompletionState.NotSatisfied);

    private static ExecutionFacts Facts(
        int iterationCount = 0,
        int retryCount = 0,
        bool deadlineExceeded = false,
        bool budgetExhausted = false,
        bool humanApprovalRequired = false,
        bool approvalPending = false,
        OutcomeDependencyState dependencyState = OutcomeDependencyState.Available,
        OutcomeAuthorityState authorityState = OutcomeAuthorityState.Authorized,
        OutcomeRoutingState routingState = OutcomeRoutingState.Available,
        bool autonomousActionAvailable = true,
        bool delegationRequired = false,
        bool pendingActions = true,
        bool externalInterventionRequired = false,
        bool verifiableProgress = false,
        bool responsibilityRetained = true,
        OutcomeCompletionState completionState = OutcomeCompletionState.NotSatisfied,
        IEnumerable<OutcomePolicyTrigger>? triggers = null) =>
        new(
            iterationCount,
            retryCount,
            deadlineExceeded,
            budgetExhausted,
            humanApprovalRequired,
            approvalPending,
            dependencyState,
            authorityState,
            routingState,
            autonomousActionAvailable,
            delegationRequired,
            pendingActions,
            externalInterventionRequired,
            verifiableProgress,
            responsibilityRetained,
            completionState,
            triggers);

    private static DirectiveExecutionContract Directive() =>
        new(
            requiredInputs: [new("input.work", "The work input is available.")],
            completionCriteria: [new("criterion.complete", "The requested work is complete.")]);

    private static OutcomePolicySnapshot Policy(
        IEnumerable<OutcomePolicyTrigger>? triggers = null) =>
        new(
            "outcome-policy-v1",
            "sha256:resolver",
            maximumIterations: 3,
            maximumRetries: 2,
            verifierEnabled: true,
            triggers ?? [OutcomePolicyTrigger.SecurityRisk]);

    private static OutcomeProposal ContinueProposal() =>
        new(
            OutcomeProposedIntent.ContinueWork,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.None,
            [],
            "Run the next authorized action.",
            Evidence());

    private static OutcomeProposal ReportProgressProposal() =>
        new(
            OutcomeProposedIntent.ReportProgress,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.None,
            [],
            "Continue with the authorized next step.",
            Evidence());

    private static OutcomeProposal ReportDoneProposal() =>
        new(
            OutcomeProposedIntent.ReportDone,
            OutcomeWorkState.Completed,
            OutcomeRequiredIntervention.None,
            [],
            nextAction: null,
            Evidence());

    private static OutcomeProposal DirectiveProposal() =>
        new(
            OutcomeProposedIntent.Directive,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.Delegation,
            [],
            "Delegate the next action.",
            Evidence());

    private static OutcomeProposal EscalationProposal() =>
        new(
            OutcomeProposedIntent.Escalation,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.SuperiorDecision,
            [OutcomeBlocker.SuperiorDecision],
            nextAction: null,
            Evidence());

    private static OutcomeProposal ApprovalProposal() =>
        new(
            OutcomeProposedIntent.ApprovalRequired,
            OutcomeWorkState.Blocked,
            OutcomeRequiredIntervention.HumanApproval,
            [OutcomeBlocker.HumanApproval],
            nextAction: null,
            Evidence());

    private static OutcomeEvidenceReference[] Evidence() =>
        [new(OutcomeEvidenceSource.RuntimeFact, "iteration.evidence")];

    public enum ObjectiveGate
    {
        Deadline,
        Budget,
        IterationLimit,
        RetryLimit,
        PermanentDependency,
        Authority,
        Routing,
        PolicyTrigger,
    }
}
