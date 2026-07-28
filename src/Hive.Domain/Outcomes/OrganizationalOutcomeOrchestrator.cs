using System.Collections.Immutable;

namespace Hive.Domain.Outcomes;

public interface IOrganizationalOutcomeOrchestrator
{
    Task<OutcomeResolution> ResolveAsync(
        OutcomeVerificationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs the pure resolver first and invokes the optional semantic verifier only for an
/// undetermined result. Every verifier boundary failure closes to an auditable escalation.
/// </summary>
public sealed class OrganizationalOutcomeOrchestrator : IOrganizationalOutcomeOrchestrator
{
    private readonly IOrganizationalOutcomeResolver _resolver;
    private readonly IOutcomeVerifier _verifier;

    public OrganizationalOutcomeOrchestrator(
        IOrganizationalOutcomeResolver resolver,
        IOutcomeVerifier verifier)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public async Task<OutcomeResolution> ResolveAsync(
        OutcomeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var initial = _resolver.Resolve(
            request.Facts,
            request.Directive,
            request.Proposal,
            request.Policy);
        var semanticCompletionEligibility =
            OutcomeSemanticCompletionEligibility.Evaluate(request);
        if (initial.Outcome != OutcomeKind.Undetermined)
        {
            return WithVerifierState(
                initial,
                semanticCompletionEligibility);
        }

        if (!request.Policy.VerifierEnabled)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierUnavailable,
                verifierInvoked: false);
        }

        // These gates normally resolve before Undetermined. Keeping them at this boundary makes
        // the verifier preconditions explicit and prevents a future resolver change from opening
        // an extra model call after budget or deadline exhaustion.
        if (request.Facts.BudgetExhausted)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.BudgetExhausted,
                verifierInvoked: false);
        }

        if (request.Facts.DeadlineExceeded)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.DeadlineExceeded,
                verifierInvoked: false);
        }

        if (request.Artifact is null)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierUnavailable,
                verifierInvoked: false,
                verifierStatus: OutcomeVerifierResultStatus.Unavailable);
        }

        OutcomeVerifierResult result;
        try
        {
            result = await _verifier
                .VerifyAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierTimedOut,
                verifierInvoked: true,
                verifierStatus: OutcomeVerifierResultStatus.TimedOut);
        }
        catch (TimeoutException)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierTimedOut,
                verifierInvoked: true,
                verifierStatus: OutcomeVerifierResultStatus.TimedOut);
        }
        catch (Exception)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierUnavailable,
                verifierInvoked: true,
                verifierStatus: OutcomeVerifierResultStatus.Unavailable);
        }

        if (result is null)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierUnavailable,
                verifierInvoked: true,
                verifierStatus: OutcomeVerifierResultStatus.Unavailable);
        }

        var failureReason = result.Status switch
        {
            OutcomeVerifierResultStatus.Unavailable =>
                OutcomeResolutionReason.VerifierUnavailable,
            OutcomeVerifierResultStatus.TimedOut =>
                OutcomeResolutionReason.VerifierTimedOut,
            OutcomeVerifierResultStatus.InvalidOutput =>
                OutcomeResolutionReason.VerifierOutputInvalid,
            OutcomeVerifierResultStatus.Classified => (OutcomeResolutionReason?)null,
            _ => OutcomeResolutionReason.VerifierOutputInvalid,
        };
        if (failureReason is { } verificationFailure)
        {
            return FailSafe(
                request,
                verificationFailure,
                verifierInvoked: true,
                verifierStatus: result.Status,
                verifierClassification: result.Classification);
        }

        if (result.Classification == OutcomeVerifierClassification.Undetermined)
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierDisagreement,
                verifierInvoked: true,
                verifierStatus: result.Status,
                verifierClassification: result.Classification);
        }

        if (!TryCreateVerifiedProposal(
            request,
            initial,
            result.Classification!.Value,
            out var verifiedProposal))
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierDisagreement,
                verifierInvoked: true,
                verifierStatus: result.Status,
                verifierClassification: result.Classification);
        }

        var verifiedFacts = WithSemanticCompletionAttestation(
            request,
            result.Classification.Value);
        var verified = _resolver.Resolve(
            verifiedFacts,
            request.Directive,
            verifiedProposal!,
            request.Policy);
        if (verified.Outcome == OutcomeKind.Undetermined)
        {
            var terminalReason = verified.Reasons.Contains(OutcomeResolutionReason.ContradictoryFacts)
                ? OutcomeResolutionReason.VerifierContradictedFacts
                : OutcomeResolutionReason.VerifierDisagreement;
            return FailSafe(
                request,
                terminalReason,
                verifierInvoked: true,
                verifierStatus: result.Status,
                verifierClassification: result.Classification);
        }

        if (verified.Outcome != ToOutcomeKind(result.Classification.Value))
        {
            return FailSafe(
                request,
                OutcomeResolutionReason.VerifierContradictedFacts,
                verifierInvoked: true,
                verifierStatus: result.Status,
                verifierClassification: result.Classification);
        }

        return new OutcomeResolution(
            verified.Outcome,
            verified.Reasons.Append(OutcomeResolutionReason.VerifierConfirmed),
            request.Policy.Version,
            request.Policy.Fingerprint,
            proposalOverridden: verified.Outcome != ToOutcomeKind(request.Proposal.ProposedIntent),
            verifierInvoked: true,
            verifierStatus: result.Status,
            verifierClassification: result.Classification,
            semanticCompletionCandidate: semanticCompletionEligibility.IsEligible,
            semanticCompletionIneligibilityReasons:
                semanticCompletionEligibility.IneligibilityReasons);
    }

    private static ExecutionFacts WithSemanticCompletionAttestation(
        OutcomeVerificationRequest request,
        OutcomeVerifierClassification classification)
    {
        if (classification != OutcomeVerifierClassification.ReportDone ||
            !OutcomeSemanticCompletionEligibility.IsEligible(request))
        {
            return request.Facts;
        }

        return request.Facts.WithCompletionState(OutcomeCompletionState.SemanticallyVerified);
    }

    private static bool TryCreateVerifiedProposal(
        OutcomeVerificationRequest request,
        OutcomeResolution initial,
        OutcomeVerifierClassification classification,
        out OutcomeProposal? proposal)
    {
        if (ToOutcomeKind(classification) == ToOutcomeKind(request.Proposal.ProposedIntent))
        {
            proposal = request.Proposal;
            return true;
        }

        var evidence = request.Proposal.EvidenceReferences;
        var verifiedReportEvidence = VerifiedReportEvidence(request);
        proposal = classification switch
        {
            OutcomeVerifierClassification.ContinueWork when request.Proposal.NextAction is not null =>
                new OutcomeProposal(
                    OutcomeProposedIntent.ContinueWork,
                    OutcomeWorkState.InProgress,
                    OutcomeRequiredIntervention.None,
                    blockers: [],
                    request.Proposal.NextAction,
                    evidence),
            OutcomeVerifierClassification.ReportProgress
                when request.Proposal.NextAction is not null && evidence.Length > 0 =>
                new OutcomeProposal(
                    OutcomeProposedIntent.ReportProgress,
                    OutcomeWorkState.InProgress,
                    OutcomeRequiredIntervention.None,
                    blockers: [],
                    request.Proposal.NextAction,
                    evidence),
            OutcomeVerifierClassification.ReportDone
                when verifiedReportEvidence.Length > 0 =>
                new OutcomeProposal(
                    OutcomeProposedIntent.ReportDone,
                    OutcomeWorkState.Completed,
                    OutcomeRequiredIntervention.None,
                    blockers: [],
                    nextAction: null,
                    verifiedReportEvidence),
            OutcomeVerifierClassification.Escalation =>
                new OutcomeProposal(
                    OutcomeProposedIntent.Escalation,
                    OutcomeWorkState.Blocked,
                    OutcomeRequiredIntervention.SuperiorDecision,
                    [EscalationBlocker(initial)],
                    nextAction: null,
                    evidence),
            OutcomeVerifierClassification.Directive when request.Proposal.NextAction is not null =>
                new OutcomeProposal(
                    OutcomeProposedIntent.Directive,
                    OutcomeWorkState.InProgress,
                    OutcomeRequiredIntervention.Delegation,
                    blockers: [],
                    request.Proposal.NextAction,
                    evidence),
            OutcomeVerifierClassification.ApprovalRequired =>
                new OutcomeProposal(
                    OutcomeProposedIntent.ApprovalRequired,
                    OutcomeWorkState.Blocked,
                    OutcomeRequiredIntervention.HumanApproval,
                    [OutcomeBlocker.HumanApproval],
                    nextAction: null,
                    evidence),
            _ => null,
        };

        return proposal is not null;
    }

    private static ImmutableArray<OutcomeEvidenceReference> VerifiedReportEvidence(
        OutcomeVerificationRequest request)
    {
        if (!request.Proposal.EvidenceReferences.IsEmpty)
        {
            return request.Proposal.EvidenceReferences;
        }

        return request.Directive.CompletionCriteria
            .Select(criterion => new OutcomeEvidenceReference(
                OutcomeEvidenceSource.CompletionCriterion,
                criterion.Reference))
            .ToImmutableArray();
    }

    private static OutcomeBlocker EscalationBlocker(OutcomeResolution initial) =>
        initial.Reasons.Contains(OutcomeResolutionReason.InsufficientFacts)
            ? OutcomeBlocker.MissingInput
            : OutcomeBlocker.SuperiorDecision;

    private static OutcomeResolution FailSafe(
        OutcomeVerificationRequest request,
        OutcomeResolutionReason reason,
        bool verifierInvoked,
        OutcomeVerifierResultStatus? verifierStatus = null,
        OutcomeVerifierClassification? verifierClassification = null)
    {
        var semanticCompletionEligibility =
            OutcomeSemanticCompletionEligibility.Evaluate(request);
        return new(
            request.Policy.FailSafeOutcome,
            [reason],
            request.Policy.Version,
            request.Policy.Fingerprint,
            proposalOverridden:
                request.Proposal.ProposedIntent != OutcomeProposedIntent.Escalation,
            verifierInvoked,
            verifierStatus,
            verifierClassification,
            semanticCompletionEligibility.IsEligible,
            semanticCompletionEligibility.IneligibilityReasons);
    }

    private static OutcomeResolution WithVerifierState(
        OutcomeResolution resolution,
        OutcomeSemanticCompletionEligibilityResult semanticCompletionEligibility) =>
        new(
            resolution.Outcome,
            resolution.Reasons,
            resolution.PolicyVersion,
            resolution.PolicyFingerprint,
            resolution.ProposalOverridden,
            resolution.VerifierInvoked,
            resolution.VerifierStatus,
            resolution.VerifierClassification,
            semanticCompletionEligibility.IsEligible,
            semanticCompletionEligibility.IneligibilityReasons);

    private static OutcomeKind ToOutcomeKind(OutcomeVerifierClassification classification) =>
        classification switch
        {
            OutcomeVerifierClassification.ContinueWork => OutcomeKind.ContinueWork,
            OutcomeVerifierClassification.ReportProgress => OutcomeKind.ReportProgress,
            OutcomeVerifierClassification.ReportDone => OutcomeKind.ReportDone,
            OutcomeVerifierClassification.Escalation => OutcomeKind.Escalation,
            OutcomeVerifierClassification.Directive => OutcomeKind.Directive,
            OutcomeVerifierClassification.ApprovalRequired => OutcomeKind.ApprovalRequired,
            OutcomeVerifierClassification.Undetermined => OutcomeKind.Undetermined,
            _ => throw new ArgumentOutOfRangeException(
                nameof(classification),
                classification,
                "Unknown verifier classification."),
        };

    private static OutcomeKind ToOutcomeKind(OutcomeProposedIntent intent) =>
        intent switch
        {
            OutcomeProposedIntent.ContinueWork => OutcomeKind.ContinueWork,
            OutcomeProposedIntent.ReportProgress => OutcomeKind.ReportProgress,
            OutcomeProposedIntent.ReportDone => OutcomeKind.ReportDone,
            OutcomeProposedIntent.Escalation => OutcomeKind.Escalation,
            OutcomeProposedIntent.Directive => OutcomeKind.Directive,
            OutcomeProposedIntent.ApprovalRequired => OutcomeKind.ApprovalRequired,
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent,
                "Unknown proposed intent."),
        };
}
