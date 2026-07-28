using System.Collections.Immutable;

namespace Hive.Domain.Outcomes;

/// <summary>
/// Resolves an organizational outcome without calling providers, tools, actors, or storage.
/// </summary>
public interface IOrganizationalOutcomeResolver
{
    OutcomeResolution Resolve(
        ExecutionFacts facts,
        DirectiveExecutionContract directive,
        OutcomeProposal proposal,
        OutcomePolicySnapshot policy);
}

/// <summary>
/// Pure, deterministic implementation of the organizational outcome precedence table.
/// </summary>
public sealed class OrganizationalOutcomeResolver : IOrganizationalOutcomeResolver
{
    public OutcomeResolution Resolve(
        ExecutionFacts facts,
        DirectiveExecutionContract directive,
        OutcomeProposal proposal,
        OutcomePolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(directive);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(policy);

        var approvalReasons = ApprovalReasons(facts);
        if (!approvalReasons.IsEmpty)
        {
            return Create(
                OutcomeKind.ApprovalRequired,
                approvalReasons,
                proposal,
                policy);
        }

        var escalationReasons = ObjectiveEscalationReasons(facts, policy);
        if (!escalationReasons.IsEmpty)
        {
            return Create(
                OutcomeKind.Escalation,
                escalationReasons,
                proposal,
                policy);
        }

        if (proposal.ProposedIntent == OutcomeProposedIntent.Escalation)
        {
            return Create(
                OutcomeKind.Escalation,
                [OutcomeResolutionReason.ProposalEscalation],
                proposal,
                policy);
        }

        // A progress report is accepted only when the proposal explicitly asks for one and
        // the runtime independently proves both the progress and the autonomous next step.
        // Without that proof, an available action remains ContinueWork rather than a report.
        if (CanReportProgress(facts, proposal))
        {
            return Create(
                OutcomeKind.ReportProgress,
                [OutcomeResolutionReason.VerifiableProgress],
                proposal,
                policy);
        }

        if (CanContinue(facts))
        {
            return Create(
                OutcomeKind.ContinueWork,
                [OutcomeResolutionReason.AutonomousActionAvailable],
                proposal,
                policy);
        }

        if (CanDelegate(facts))
        {
            return Create(
                OutcomeKind.Directive,
                [OutcomeResolutionReason.DelegationRequired],
                proposal,
                policy);
        }

        if (CanReportDone(facts, proposal))
        {
            return Create(
                OutcomeKind.ReportDone,
                [facts.CompletionState == OutcomeCompletionState.SemanticallyVerified
                    ? OutcomeResolutionReason.SemanticCompletionVerified
                    : OutcomeResolutionReason.CompletionCriteriaSatisfied],
                proposal,
                policy);
        }

        return Create(
            OutcomeKind.Undetermined,
            [ResolutionFailureReason(facts, directive, proposal)],
            proposal,
            policy);
    }

    private static ImmutableArray<OutcomeResolutionReason> ApprovalReasons(ExecutionFacts facts)
    {
        var reasons = ImmutableArray.CreateBuilder<OutcomeResolutionReason>();
        if (facts.HumanApprovalRequired)
        {
            reasons.Add(OutcomeResolutionReason.HumanApprovalGate);
        }

        if (facts.ApprovalPending)
        {
            reasons.Add(OutcomeResolutionReason.ApprovalPending);
        }

        return reasons.ToImmutable();
    }

    private static ImmutableArray<OutcomeResolutionReason> ObjectiveEscalationReasons(
        ExecutionFacts facts,
        OutcomePolicySnapshot policy)
    {
        var reasons = ImmutableArray.CreateBuilder<OutcomeResolutionReason>();
        if (facts.DeadlineExceeded)
        {
            reasons.Add(OutcomeResolutionReason.DeadlineExceeded);
        }

        if (facts.BudgetExhausted)
        {
            reasons.Add(OutcomeResolutionReason.BudgetExhausted);
        }

        if (facts.IterationCount >= policy.MaximumIterations)
        {
            reasons.Add(OutcomeResolutionReason.IterationLimitReached);
        }

        if (facts.RetryCount >= policy.MaximumRetries)
        {
            reasons.Add(OutcomeResolutionReason.RetryLimitReached);
        }

        if (facts.DependencyState == OutcomeDependencyState.PermanentFailure)
        {
            reasons.Add(OutcomeResolutionReason.PermanentDependencyFailure);
        }

        if (facts.AuthorityState == OutcomeAuthorityState.Denied)
        {
            reasons.Add(OutcomeResolutionReason.AuthorityDenied);
        }

        if (facts.RoutingState == OutcomeRoutingState.Unavailable)
        {
            reasons.Add(OutcomeResolutionReason.RoutingUnavailable);
        }

        if (facts.ObservedPolicyTriggers.Any(policy.EscalationTriggers.Contains))
        {
            reasons.Add(OutcomeResolutionReason.PolicyTriggerObserved);
        }

        return reasons.ToImmutable();
    }

    private static bool CanContinue(ExecutionFacts facts) =>
        facts.AutonomousActionAvailable &&
        facts.PendingActions &&
        facts.ResponsibilityRetained &&
        !facts.ExternalInterventionRequired &&
        facts.DependencyState == OutcomeDependencyState.Available &&
        HasAuthority(facts) &&
        HasRouting(facts);

    private static bool CanDelegate(ExecutionFacts facts) =>
        facts.DelegationRequired &&
        facts.PendingActions &&
        !facts.ExternalInterventionRequired &&
        facts.DependencyState == OutcomeDependencyState.Available &&
        HasAuthority(facts) &&
        HasRouting(facts);

    private static bool CanReportDone(ExecutionFacts facts, OutcomeProposal proposal) =>
        proposal.ProposedIntent == OutcomeProposedIntent.ReportDone &&
        facts.CompletionState is OutcomeCompletionState.Satisfied or
            OutcomeCompletionState.SemanticallyVerified &&
        !facts.PendingActions &&
        !facts.AutonomousActionAvailable &&
        !facts.DelegationRequired &&
        !facts.ExternalInterventionRequired &&
        HasAuthority(facts) &&
        HasRouting(facts) &&
        facts.DependencyState == OutcomeDependencyState.Available;

    private static bool CanReportProgress(ExecutionFacts facts, OutcomeProposal proposal) =>
        proposal.ProposedIntent == OutcomeProposedIntent.ReportProgress &&
        facts.VerifiableProgress &&
        facts.ResponsibilityRetained &&
        facts.PendingActions &&
        facts.AutonomousActionAvailable &&
        !facts.DelegationRequired &&
        !facts.ExternalInterventionRequired &&
        facts.CompletionState is not OutcomeCompletionState.Satisfied and
            not OutcomeCompletionState.SemanticallyVerified &&
        facts.DependencyState == OutcomeDependencyState.Available &&
        HasAuthority(facts) &&
        HasRouting(facts);

    private static bool HasAuthority(ExecutionFacts facts) =>
        facts.AuthorityState is OutcomeAuthorityState.NotRequired or OutcomeAuthorityState.Authorized;

    private static bool HasRouting(ExecutionFacts facts) =>
        facts.RoutingState is OutcomeRoutingState.NotRequired or OutcomeRoutingState.Available;

    private static OutcomeResolutionReason ResolutionFailureReason(
        ExecutionFacts facts,
        DirectiveExecutionContract directive,
        OutcomeProposal proposal)
    {
        if (facts.AuthorityState == OutcomeAuthorityState.Unknown ||
            facts.RoutingState == OutcomeRoutingState.Unknown ||
            facts.DependencyState == OutcomeDependencyState.TransientFailure ||
            facts.CompletionState == OutcomeCompletionState.Unknown ||
            (directive.CompletionCriteria.Length > 0 &&
             facts.CompletionState == OutcomeCompletionState.NotDeclared))
        {
            return OutcomeResolutionReason.InsufficientFacts;
        }

        var contradictory = proposal.ProposedIntent switch
        {
            OutcomeProposedIntent.ContinueWork =>
                !facts.AutonomousActionAvailable ||
                !facts.PendingActions ||
                !facts.ResponsibilityRetained ||
                facts.ExternalInterventionRequired,
            OutcomeProposedIntent.ReportProgress =>
                !facts.VerifiableProgress ||
                !facts.ResponsibilityRetained ||
                !facts.PendingActions ||
                facts.DelegationRequired ||
                facts.ExternalInterventionRequired,
            OutcomeProposedIntent.ReportDone =>
                facts.CompletionState == OutcomeCompletionState.NotSatisfied ||
                facts.PendingActions ||
                facts.AutonomousActionAvailable ||
                facts.DelegationRequired ||
                facts.ExternalInterventionRequired,
            OutcomeProposedIntent.Directive => !facts.DelegationRequired,
            OutcomeProposedIntent.ApprovalRequired =>
                !facts.HumanApprovalRequired && !facts.ApprovalPending,
            _ => false,
        };

        return contradictory
            ? OutcomeResolutionReason.ContradictoryFacts
            : OutcomeResolutionReason.InsufficientFacts;
    }

    private static OutcomeResolution Create(
        OutcomeKind outcome,
        IEnumerable<OutcomeResolutionReason> reasons,
        OutcomeProposal proposal,
        OutcomePolicySnapshot policy) =>
        new(
            outcome,
            reasons,
            policy.Version,
            policy.Fingerprint,
            proposalOverridden: outcome != ProposedOutcome(proposal.ProposedIntent),
            verifierInvoked: false);

    private static OutcomeKind ProposedOutcome(OutcomeProposedIntent intent) =>
        intent switch
        {
            OutcomeProposedIntent.ContinueWork => OutcomeKind.ContinueWork,
            OutcomeProposedIntent.ReportProgress => OutcomeKind.ReportProgress,
            OutcomeProposedIntent.ReportDone => OutcomeKind.ReportDone,
            OutcomeProposedIntent.Escalation => OutcomeKind.Escalation,
            OutcomeProposedIntent.Directive => OutcomeKind.Directive,
            OutcomeProposedIntent.ApprovalRequired => OutcomeKind.ApprovalRequired,
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown proposal intent."),
        };
}
