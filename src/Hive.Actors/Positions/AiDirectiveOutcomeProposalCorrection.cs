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

    private static readonly string AuthorityReferencePath =
        $"{AiDirectiveOutcomeProposalEnvelope.PropertyName}." +
        $"{OutcomeProposalConstraint.ProposalProperty}." +
        $"{OutcomeProposalConstraint.AuthorityRequestProperty}." +
        OutcomeProposalConstraint.AuthorityReferenceProperty;

    public static bool IsEligible(
        AiDirectiveInterpretationResult interpretation,
        OutcomeProposalAuthorityContext authorityContext)
    {
        ArgumentNullException.ThrowIfNull(interpretation);
        ArgumentNullException.ThrowIfNull(authorityContext);

        var errors = interpretation.Failure?.ParseErrors ?? [];
        return interpretation.RequiresEscalation &&
            errors is { IsEmpty: false, Length: <= MaximumDiagnostics } &&
            (errors.All(IsEvidenceDiagnostic) ||
             (authorityContext.HasReferences &&
              errors.Length == 1 &&
              errors.All(IsAuthorityReferenceDiagnostic)));
    }

    public static string CreateBoundedInstruction(
        OutcomeProposalEvidenceContext evidenceContext,
        OutcomeProposalAuthorityContext authorityContext,
        IEnumerable<AiDirectiveDecisionParseError> parseErrors,
        AiDirectiveDecision? acceptedDecision = null,
        OutcomeProposal? acceptedProposal = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceContext);
        ArgumentNullException.ThrowIfNull(authorityContext);
        ArgumentNullException.ThrowIfNull(parseErrors);

        var diagnostics = parseErrors
            .Distinct()
            .OrderBy(error => error.Path, StringComparer.Ordinal)
            .ThenBy(error => error.Code, StringComparer.Ordinal)
            .ToImmutableArray();
        var evidenceCorrection = diagnostics.All(IsEvidenceDiagnostic);
        var authorityCorrection = diagnostics.Length == 1 &&
            diagnostics.All(IsAuthorityReferenceDiagnostic) &&
            authorityContext.HasReferences;
        if (diagnostics.IsEmpty ||
            diagnostics.Length > MaximumDiagnostics ||
            (!evidenceCorrection && !authorityCorrection))
        {
            throw new ArgumentException(
                "Outcome proposal correction requires one bounded diagnostic category.",
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

        var correctionLines = evidenceCorrection
            ? EvidenceCorrectionLines(references)
            : AuthorityCorrectionLines(authorityContext);
        var instruction = string.Join(
            Environment.NewLine,
            [
                authorityCorrection
                    ? "OutcomeProposal authority correction"
                    : "OutcomeProposal evidence correction",
                "PreviousOperation: structured-output-correction",
                $"AcceptedResult: {AcceptedResult(acceptedDecision)}",
                $"AcceptedProposal: {AcceptedProposal(acceptedProposal)}",
                "PreviousResolution: <not-run>",
                "The previous proposal was rejected locally before verification.",
                $"Closed diagnostics: {diagnosticList}.",
                .. correctionLines,
                "Return one complete replacement response under the enforced schema.",
                "The runtime has not copied, substituted, or rewritten the rejected reference and does not expose its rejected value.",
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

    public static string CorrectionKind(
        IEnumerable<AiDirectiveDecisionParseError> parseErrors)
    {
        ArgumentNullException.ThrowIfNull(parseErrors);
        return parseErrors.Any(IsAuthorityReferenceDiagnostic)
            ? "outcome-proposal-authority"
            : "outcome-proposal-evidence";
    }

    private static string[] EvidenceCorrectionLines(string references) =>
    [
        $"The only allowed evidence source is \"{OutcomeEvidenceSourceContract.ToWireValue(OutcomeEvidenceSource.DirectiveInput)}\".",
        $"Allowed exact evidence references: {references}.",
        "Cite only allowed references that actually support the replacement. If none support the same outcome, return a different compatible organizational decision and proposal instead of fabricating evidence.",
    ];

    private static string[] AuthorityCorrectionLines(
        OutcomeProposalAuthorityContext authorityContext) =>
    [
        $"Allowed exact ActionDomain references: {AuthorityReferences(authorityContext, OutcomeAuthorityKind.ActionDomain)}.",
        $"Allowed exact ApprovalPolicy references: {AuthorityReferences(authorityContext, OutcomeAuthorityKind.ApprovalPolicy)}.",
        "Pair authority_kind with a reference from its matching allowlist. If no listed reference applies to the same request, return a different compatible organizational decision and proposal instead of fabricating authority.",
    ];

    private static string AuthorityReferences(
        OutcomeProposalAuthorityContext authorityContext,
        OutcomeAuthorityKind kind)
    {
        var references = authorityContext.ReferencesFor(kind);
        return references.IsEmpty
            ? "<empty>"
            : string.Join(
                ", ",
                references.Select(reference => JsonSerializer.Serialize(reference)));
    }

    private static bool IsEvidenceDiagnostic(AiDirectiveDecisionParseError error) =>
        error.Path.StartsWith(EvidencePathPrefix, StringComparison.Ordinal);

    private static bool IsAuthorityReferenceDiagnostic(
        AiDirectiveDecisionParseError error) =>
        string.Equals(error.Path, AuthorityReferencePath, StringComparison.Ordinal);

    private static string AcceptedResult(AiDirectiveDecision? decision) =>
        decision switch
        {
            AiDirectiveReportDecision report => AcceptedReport(report),
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

    private static string AcceptedReport(AiDirectiveReportDecision report)
    {
        var kind = ReportKindContract.RequireDefined(report.Kind, nameof(report.Kind)).ToString();
        if (report.Checkpoint is null)
        {
            return JsonSerializer.Serialize(new
            {
                intent = "Report",
                kind,
                body = report.Body,
            });
        }

        var checkpoint = report.Checkpoint;
        return JsonSerializer.Serialize(new
        {
            intent = "Report",
            kind,
            body = report.Body,
            checkpoint = new
            {
                contract_version = checkpoint.ContractVersion,
                plan = new
                {
                    contract_version = checkpoint.Plan.ContractVersion,
                    subtasks = checkpoint.Plan.Subtasks.Select(subtask => new
                    {
                        sequence = subtask.Sequence,
                        local_id = subtask.LocalId,
                        objective = subtask.Objective,
                        completion_criteria = subtask.CompletionCriteria,
                        estimated_duration_ms = (long)subtask.EstimatedDuration.TotalMilliseconds,
                    }),
                },
                completed_subtasks = checkpoint.CompletedSubtasks.Select(completed => new
                {
                    local_id = completed.LocalId,
                    evidence_references = completed.EvidenceReferences.Select(reference => new
                    {
                        source = OutcomeEvidenceSourceContract.ToWireValue(reference.Source),
                        reference = reference.Reference,
                    }),
                }),
                blockers = checkpoint.Blockers.Select(OutcomeBlockerContract.ToWireValue),
                next_subtask_id = checkpoint.NextSubtaskId,
            },
        });
    }

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
                information_gaps = proposal.InformationGaps.Select(gap => new
                {
                    missing_evidence_reference = gap.MissingEvidenceReference,
                    materiality = OutcomeInformationGapMaterialityContract.ToWireValue(
                        gap.Materiality),
                    materiality_reason = gap.MaterialityReason is { } reason
                        ? OutcomeInformationGapMaterialityReasonContract.ToWireValue(reason)
                        : null,
                }),
                authority_request = proposal.AuthorityRequest is { } authorityRequest
                    ? new
                    {
                        decision = authorityRequest.Decision,
                        authority_kind = OutcomeAuthorityKindContract.ToWireValue(
                            authorityRequest.AuthorityKind),
                        authority_reference = authorityRequest.AuthorityReference,
                        position_limit_reason = authorityRequest.PositionLimitReason,
                    }
                    : null,
            });
}
