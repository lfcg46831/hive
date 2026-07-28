using System.Collections.Immutable;

namespace Hive.Domain.Outcomes;

public enum OutcomeSemanticCompletionIneligibilityReason
{
    ProposalIntentNotReportDone = 1,
    WorkStateNotCompleted = 2,
    InterventionRequired = 3,
    BlockersPresent = 4,
    NextActionPresent = 5,
    StructuredCompletionCriteriaPresent = 6,
    CompletionStateIncompatible = 7,
    EvidenceReferencesMissing = 8,
    EvidenceSourceNotDirectiveInput = 9,
    EvidenceReferenceNotInContext = 10,
}

public static class OutcomeSemanticCompletionIneligibilityReasonContract
{
    private static readonly OutcomeEnumWireContract<
        OutcomeSemanticCompletionIneligibilityReason> Contract = new(
        (OutcomeSemanticCompletionIneligibilityReason.ProposalIntentNotReportDone,
            "proposal-intent-not-report-done"),
        (OutcomeSemanticCompletionIneligibilityReason.WorkStateNotCompleted,
            "work-state-not-completed"),
        (OutcomeSemanticCompletionIneligibilityReason.InterventionRequired,
            "intervention-required"),
        (OutcomeSemanticCompletionIneligibilityReason.BlockersPresent, "blockers-present"),
        (OutcomeSemanticCompletionIneligibilityReason.NextActionPresent, "next-action-present"),
        (OutcomeSemanticCompletionIneligibilityReason.StructuredCompletionCriteriaPresent,
            "structured-completion-criteria-present"),
        (OutcomeSemanticCompletionIneligibilityReason.CompletionStateIncompatible,
            "completion-state-incompatible"),
        (OutcomeSemanticCompletionIneligibilityReason.EvidenceReferencesMissing,
            "evidence-references-missing"),
        (OutcomeSemanticCompletionIneligibilityReason.EvidenceSourceNotDirectiveInput,
            "evidence-source-not-directive-input"),
        (OutcomeSemanticCompletionIneligibilityReason.EvidenceReferenceNotInContext,
            "evidence-reference-not-in-context"));

    public static ImmutableArray<string> WireValues => Contract.WireValues;

    public static OutcomeSemanticCompletionIneligibilityReason RequireDefined(
        OutcomeSemanticCompletionIneligibilityReason value,
        string parameterName) => Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(OutcomeSemanticCompletionIneligibilityReason value) =>
        Contract.ToWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out OutcomeSemanticCompletionIneligibilityReason result) =>
        Contract.TryParseWireValue(value, out result);
}

public sealed record OutcomeSemanticCompletionEligibilityResult
{
    public OutcomeSemanticCompletionEligibilityResult(
        IEnumerable<OutcomeSemanticCompletionIneligibilityReason>? ineligibilityReasons)
    {
        IneligibilityReasons = (ineligibilityReasons ?? [])
            .Select(reason =>
                OutcomeSemanticCompletionIneligibilityReasonContract.RequireDefined(
                    reason,
                    nameof(ineligibilityReasons)))
            .Distinct()
            .Order()
            .ToImmutableArray();
    }

    public bool IsEligible => IneligibilityReasons.IsEmpty;

    public ImmutableArray<OutcomeSemanticCompletionIneligibilityReason>
        IneligibilityReasons { get; }
}

/// <summary>
/// Evaluates only the closed structural preconditions for the limited semantic-completion
/// attestation. It does not decide whether the bounded context semantically supports completion
/// and it never replaces authoritative execution facts or structured completion evidence.
/// </summary>
public static class OutcomeSemanticCompletionEligibility
{
    public static OutcomeSemanticCompletionEligibilityResult Evaluate(
        OutcomeVerificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reasons = ImmutableArray.CreateBuilder<
            OutcomeSemanticCompletionIneligibilityReason>();
        if (request.Proposal.ProposedIntent != OutcomeProposedIntent.ReportDone)
        {
            reasons.Add(
                OutcomeSemanticCompletionIneligibilityReason.ProposalIntentNotReportDone);
        }

        if (request.Proposal.WorkState != OutcomeWorkState.Completed)
        {
            reasons.Add(OutcomeSemanticCompletionIneligibilityReason.WorkStateNotCompleted);
        }

        if (request.Proposal.RequiredIntervention != OutcomeRequiredIntervention.None)
        {
            reasons.Add(OutcomeSemanticCompletionIneligibilityReason.InterventionRequired);
        }

        if (!request.Proposal.Blockers.IsEmpty)
        {
            reasons.Add(OutcomeSemanticCompletionIneligibilityReason.BlockersPresent);
        }

        if (request.Proposal.NextAction is not null)
        {
            reasons.Add(OutcomeSemanticCompletionIneligibilityReason.NextActionPresent);
        }

        if (!request.Directive.CompletionCriteria.IsEmpty)
        {
            reasons.Add(
                OutcomeSemanticCompletionIneligibilityReason
                    .StructuredCompletionCriteriaPresent);
        }

        if (request.Facts.CompletionState != OutcomeCompletionState.NotDeclared)
        {
            reasons.Add(
                OutcomeSemanticCompletionIneligibilityReason.CompletionStateIncompatible);
        }

        if (request.Proposal.EvidenceReferences.IsEmpty)
        {
            reasons.Add(
                OutcomeSemanticCompletionIneligibilityReason.EvidenceReferencesMissing);
            return new OutcomeSemanticCompletionEligibilityResult(reasons);
        }

        var knownReferences = request.Context.Entries
            .Select(entry => entry.Reference)
            .ToHashSet(StringComparer.Ordinal);
        if (request.Proposal.EvidenceReferences.Any(evidence =>
            evidence.Source != OutcomeEvidenceSource.DirectiveInput))
        {
            reasons.Add(
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceSourceNotDirectiveInput);
        }

        if (request.Proposal.EvidenceReferences.Any(evidence =>
            !knownReferences.Contains(evidence.Reference)))
        {
            reasons.Add(
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceReferenceNotInContext);
        }

        return new OutcomeSemanticCompletionEligibilityResult(reasons);
    }

    public static bool IsEligible(OutcomeVerificationRequest request) =>
        Evaluate(request).IsEligible;
}
