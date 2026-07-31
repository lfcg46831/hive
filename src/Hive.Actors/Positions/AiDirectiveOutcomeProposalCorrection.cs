using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

internal static class AiDirectiveOutcomeProposalCorrection
{
    public const int MaximumDiagnostics = 4;
    public const int MaximumProjectionUtf8Bytes = 4096;

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
        IEnumerable<AiDirectiveDecisionParseError> parseErrors,
        AiDirectiveDecision? acceptedDecision = null,
        OutcomeProposal? acceptedProposal = null)
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

        var instruction = string.Join(
            Environment.NewLine,
            [
                "OutcomeProposal evidence correction",
                "PreviousOperation: structured-output-correction",
                $"AcceptedResult: {AcceptedResult(acceptedDecision)}",
                $"AcceptedProposal: {AcceptedProposal(acceptedProposal)}",
                "PreviousResolution: <not-run>",
                "The previous proposal was rejected locally before verification.",
                $"Closed diagnostics: {diagnosticList}.",
                $"The only allowed evidence source is \"{OutcomeEvidenceSourceContract.ToWireValue(OutcomeEvidenceSource.DirectiveInput)}\".",
                $"Allowed exact evidence references: {references}.",
                "Return one complete replacement response under the enforced schema.",
                "The runtime has not copied, substituted, or rewritten the rejected evidence and does not expose its rejected values.",
                "Cite only allowed references that actually support the replacement. If none support the same outcome, return a different compatible organizational decision and proposal instead of fabricating evidence.",
            ]);
        var utf8Bytes = Encoding.UTF8.GetByteCount(instruction);
        if (utf8Bytes > MaximumProjectionUtf8Bytes)
        {
            throw new ArgumentException(
                $"Outcome proposal correction projection exceeds {MaximumProjectionUtf8Bytes} UTF-8 bytes.",
                nameof(acceptedDecision));
        }

        return instruction;
    }

    private static string AcceptedResult(AiDirectiveDecision? decision) =>
        decision switch
        {
            AiDirectiveReportDecision report => JsonSerializer.Serialize(new
            {
                intent = "Report",
                kind = ReportKindContract.RequireDefined(report.Kind, nameof(report.Kind)).ToString(),
                body = report.Body,
            }),
            AiDirectiveEscalationDecision escalation => JsonSerializer.Serialize(new
            {
                intent = "Escalation",
                issue = escalation.Issue,
                context = escalation.Context,
                options_considered = escalation.OptionsConsidered,
            }),
            AiDirectiveChildDirectiveDecision directive => JsonSerializer.Serialize(new
            {
                intent = "Directive",
                target_position_id = directive.TargetPositionId.Value,
                objective = directive.Objective,
                context = directive.Context,
            }),
            null => "<none>",
            _ => throw new InvalidOperationException("Unknown accepted directive result."),
        };

    private static string AcceptedProposal(OutcomeProposal? proposal) =>
        proposal is null
            ? "<none>"
            : JsonSerializer.Serialize(new
            {
                proposed_intent = OutcomeProposedIntentContract.ToWireValue(
                    proposal.ProposedIntent),
                work_state = OutcomeWorkStateContract.ToWireValue(proposal.WorkState),
                required_intervention = OutcomeRequiredInterventionContract.ToWireValue(
                    proposal.RequiredIntervention),
                blockers = proposal.Blockers.Select(OutcomeBlockerContract.ToWireValue),
                next_action = proposal.NextAction,
                evidence_references = proposal.EvidenceReferences.Select(reference => new
                {
                    source = OutcomeEvidenceSourceContract.ToWireValue(reference.Source),
                    reference = reference.Reference,
                }),
            });
}
