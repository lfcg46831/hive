using System.Collections.Immutable;
using System.Text.Json;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

internal static class AiDirectiveOutcomeProposalCorrection
{
    public const int MaximumDiagnostics = 4;

    private static readonly string EvidencePathPrefix =
        $"{AiDirectiveOutcomeProposalEnvelope.PropertyName}." +
        $"{OutcomeProposalConstraint.ProposalProperty}." +
        OutcomeProposalConstraint.EvidenceReferencesProperty;

    public static bool IsEligible(AiDirectiveInterpretationResult interpretation)
    {
        ArgumentNullException.ThrowIfNull(interpretation);

        var errors = interpretation.Failure?.ParseErrors ?? [];
        return interpretation.RequiresEscalation &&
            errors is { IsEmpty: false, Length: <= MaximumDiagnostics } &&
            errors.All(error =>
                error.Path.StartsWith(EvidencePathPrefix, StringComparison.Ordinal));
    }

    public static string CreateBoundedInstruction(
        OutcomeProposalEvidenceContext evidenceContext,
        IEnumerable<AiDirectiveDecisionParseError> parseErrors)
    {
        ArgumentNullException.ThrowIfNull(evidenceContext);
        ArgumentNullException.ThrowIfNull(parseErrors);

        var diagnostics = parseErrors
            .Distinct()
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Code, StringComparer.Ordinal)
            .ToImmutableArray();
        if (diagnostics.IsEmpty ||
            diagnostics.Length > MaximumDiagnostics ||
            diagnostics.Any(error =>
                !error.Path.StartsWith(EvidencePathPrefix, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Outcome proposal correction requires only bounded evidence diagnostics.",
                nameof(parseErrors));
        }

        var references = evidenceContext.DirectiveInputReferences.IsEmpty
            ? "<empty>"
            : string.Join(
                ", ",
                evidenceContext.DirectiveInputReferences.Select(
                    reference => JsonSerializer.Serialize(reference)));
        var diagnosticList = string.Join(
            ", ",
            diagnostics.Select(error => $"{error.Path}:{error.Code}"));

        return string.Join(
            Environment.NewLine,
            [
                "OutcomeProposal evidence correction",
                "The previous proposal was rejected locally before verification.",
                $"Closed diagnostics: {diagnosticList}.",
                $"The only allowed evidence source is \"{OutcomeEvidenceSourceContract.ToWireValue(OutcomeEvidenceSource.DirectiveInput)}\".",
                $"Allowed exact evidence references: {references}.",
                "Return one complete replacement response under the enforced schema.",
                "The runtime has not copied, substituted, or rewritten the rejected evidence and does not expose its rejected values.",
                "Cite only allowed references that actually support the replacement. If none support the same outcome, return a different compatible organizational decision and proposal instead of fabricating evidence.",
            ]);
    }
}
