using Hive.Actors.Positions;
using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class AiDirectiveOutcomeProposalEnvelopeTests
{
    private const string DoneResponse = """
        {
          "schema_version": 1,
          "acting_under": "delivery.bug-triage",
          "decision": {
            "intent": "Report",
            "report": { "kind": "Done", "body": "Triage completed." }
          },
          "outcome_proposal": {
            "schema_version": 2,
            "proposal": {
              "proposed_intent": "Report.Done",
              "work_state": "Completed",
              "required_intervention": "None",
              "blockers": [],
              "next_action": null,
              "evidence_references": [
                { "source": "DirectiveInput", "reference": "directive.context" }
              ]
            }
          }
        }
        """;

    [Fact]
    public void Compose_requires_the_complete_v2_proposal_without_mutating_the_base_schema()
    {
        var composed = AiDirectiveOutcomeProposalEnvelope.ComposeOutputConstraint(
            AiDirectiveDecisionSchema.OutputConstraint);

        Assert.Contains(
            AiDirectiveOutcomeProposalEnvelope.PropertyName,
            composed.JsonSchema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()));
        var nested = composed.JsonSchema.GetProperty("properties")
            .GetProperty(AiDirectiveOutcomeProposalEnvelope.PropertyName);
        Assert.Equal(
            OutcomeProposalConstraint.SchemaVersion,
            nested.GetProperty("properties")
                .GetProperty(OutcomeProposalConstraint.SchemaVersionProperty)
                .GetProperty("const")
                .GetInt32());
        Assert.False(AiDirectiveDecisionSchema.OutputConstraint.JsonSchema
            .GetProperty("properties")
            .TryGetProperty(AiDirectiveOutcomeProposalEnvelope.PropertyName, out _));
    }

    [Fact]
    public void Parser_returns_the_validated_v2_proposal_with_the_functional_decision()
    {
        var result = AiDirectiveDecisionParser.Parse(
            DoneResponse,
            requireOutcomeProposal: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(OutcomeProposedIntent.ReportDone, result.Proposal!.ProposedIntent);
        Assert.Equal(OutcomeWorkState.Completed, result.Proposal.WorkState);
        Assert.Equal("directive.context", Assert.Single(result.Proposal.EvidenceReferences).Reference);
    }

    [Fact]
    public void Parser_fails_closed_when_the_required_proposal_is_absent()
    {
        const string output = """
            {"schema_version":1,"acting_under":"delivery.bug-triage","decision":{"intent":"Report","report":{"kind":"Done","body":"Triage completed."}}}
            """;

        var result = AiDirectiveDecisionParser.Parse(
            output,
            requireOutcomeProposal: true);

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, error =>
            error.Code == "required-field" && error.Path == "outcome_proposal");
    }

    [Fact]
    public void Parser_rejects_a_proposal_that_contradicts_the_message_kind()
    {
        var output = DoneResponse.Replace(
            "\"proposed_intent\": \"Report.Done\"",
            "\"proposed_intent\": \"Report.Progress\"");
        output = output
            .Replace("\"work_state\": \"Completed\"", "\"work_state\": \"InProgress\"")
            .Replace("\"next_action\": null", "\"next_action\": \"Continue triage.\"");

        var result = AiDirectiveDecisionParser.Parse(
            output,
            requireOutcomeProposal: true);

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, error =>
            error.Code == "contradictory-combination" &&
            error.Path == "outcome_proposal.proposal.proposed_intent");
    }

    [Fact]
    public void Parser_rejects_duplicate_top_level_proposals()
    {
        var proposalJson = DoneResponse[DoneResponse.IndexOf(
            "{\n    \"schema_version\": 2",
            StringComparison.Ordinal)..^2];
        var output = DoneResponse.TrimEnd()[..^1] +
            ",\n  \"outcome_proposal\": " + proposalJson + "\n}";

        var result = AiDirectiveDecisionParser.Parse(
            output,
            requireOutcomeProposal: true);

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, error =>
            error.Code == "duplicate-field" && error.Path == "outcome_proposal");
    }
}
