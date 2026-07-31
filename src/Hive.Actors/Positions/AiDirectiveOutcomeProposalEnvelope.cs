using System.Text.Json;
using System.Text.Json.Nodes;
using Hive.Domain.Ai;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

/// <summary>
/// Composes the non-authoritative OutcomeProposal v2 contract into an AI directive response.
/// The organizational message remains the functional payload; the proposal is parsed and
/// resolved independently before that message can be emitted.
/// </summary>
internal static class AiDirectiveOutcomeProposalEnvelope
{
    public const string PropertyName = "outcome_proposal";

    public static AiOutputConstraint ComposeOutputConstraint(
        AiOutputConstraint baseConstraint,
        OutcomeProposalEvidenceContext? evidenceContext = null,
        bool allowProgressReports = false)
    {
        ArgumentNullException.ThrowIfNull(baseConstraint);

        var root = JsonNode.Parse(baseConstraint.JsonSchema.GetRawText())!.AsObject();
        var proposalConstraint = evidenceContext is null
            ? OutcomeProposalConstraint.CreateOutputConstraint(allowProgressReports)
            : OutcomeProposalConstraint.CreateOutputConstraint(
                evidenceContext,
                allowProgressReports);
        root["properties"]!.AsObject()[PropertyName] =
            JsonNode.Parse(proposalConstraint.JsonSchema.GetRawText());
        root["required"]!.AsArray().Add(PropertyName);

        using var document = JsonDocument.Parse(root.ToJsonString());
        return new AiOutputConstraint(
            $"{baseConstraint.SchemaName}_outcome_proposal_v2",
            baseConstraint.SchemaVersion,
            document.RootElement,
            baseConstraint.AllowedFallbackModes);
    }

    public static bool IsCompatible(AiDirectiveDecision decision, OutcomeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(proposal);

        return decision switch
        {
            AiDirectiveReportDecision { Kind: ReportKind.Done } =>
                proposal.ProposedIntent == OutcomeProposedIntent.ReportDone,
            AiDirectiveReportDecision { Kind: ReportKind.Progress } =>
                proposal.ProposedIntent is OutcomeProposedIntent.ReportProgress or
                    OutcomeProposedIntent.ContinueWork,
            AiDirectiveEscalationDecision =>
                proposal.ProposedIntent is OutcomeProposedIntent.Escalation or
                    OutcomeProposedIntent.ApprovalRequired,
            AiDirectiveChildDirectiveDecision =>
                proposal.ProposedIntent == OutcomeProposedIntent.Directive,
            _ => false,
        };
    }
}
