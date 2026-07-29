using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class OutcomeProposalParserTests
{
    public static TheoryData<string, OutcomeProposedIntent> ValidProposals => new()
    {
        {
            Envelope(
                "ContinueWork",
                "NotStarted",
                "None",
                "[]",
                "\"Perform the next authorized step.\"",
                "[]"),
            OutcomeProposedIntent.ContinueWork
        },
        {
            Envelope(
                "Report.Progress",
                "InProgress",
                "None",
                "[]",
                "\"Continue the authorized investigation.\"",
                "[{\"source\":\"RuntimeFact\",\"reference\":\"iteration.progress\"}]"),
            OutcomeProposedIntent.ReportProgress
        },
        {
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[{\"source\":\"CompletionCriterion\",\"reference\":\"criterion.done\"}]"),
            OutcomeProposedIntent.ReportDone
        },
        {
            Envelope(
                "Escalation",
                "Blocked",
                "SuperiorDecision",
                "[\"MissingInput\",\"SuperiorDecision\"]",
                null,
                "[{\"source\":\"DirectiveInput\",\"reference\":\"input.required\"}]"),
            OutcomeProposedIntent.Escalation
        },
        {
            Envelope(
                "Directive",
                "InProgress",
                "Delegation",
                "[]",
                "\"Delegate the authorized action.\"",
                "[{\"source\":\"PersistedState\",\"reference\":\"routing.child\"}]"),
            OutcomeProposedIntent.Directive
        },
        {
            Envelope(
                "ApprovalRequired",
                "Blocked",
                "HumanApproval",
                "[\"HumanApproval\"]",
                null,
                "[{\"source\":\"RuntimeFact\",\"reference\":\"approval.pending\"}]"),
            OutcomeProposedIntent.ApprovalRequired
        },
    };

    public static TheoryData<string, string, string> InvalidOutputs => new()
    {
        { "", "empty-response", "$" },
        { "{", "invalid-json", "$" },
        { "[]", "top-level-object-required", "$" },
        { "{\"proposal\":{}}", "required-field", "schema_version" },
        { "{\"schema_version\":1,\"proposal\":{}}", "invalid-schema-version", "schema_version" },
        { "{\"schema_version\":2}", "required-field", "proposal" },
        { "{\"schema_version\":2,\"proposal\":null}", "invalid-field", "proposal" },
        {
            "{\"schema_version\":2,\"proposal\":{\"proposed_intent\":\"ContinueWork\",\"work_state\":\"InProgress\",\"required_intervention\":\"None\",\"blockers\":[],\"evidence_references\":[]}}",
            "required-field",
            "proposal.next_action"
        },
        {
            Envelope("Report", "InProgress", "None", "[]", "\"Continue.\"", "[]"),
            "invalid-vocabulary",
            "proposal.proposed_intent"
        },
        {
            Envelope("ContinueWork", "Working", "None", "[]", "\"Continue.\"", "[]"),
            "invalid-vocabulary",
            "proposal.work_state"
        },
        {
            Envelope("ContinueWork", "InProgress", "Unknown", "[]", "\"Continue.\"", "[]"),
            "invalid-vocabulary",
            "proposal.required_intervention"
        },
        {
            Envelope("ContinueWork", "InProgress", "None", "[\"Other\"]", "\"Continue.\"", "[]"),
            "invalid-vocabulary",
            "proposal.blockers.item"
        },
        {
            Envelope("ContinueWork", "InProgress", "None", "[]", "\" \\t \"", "[]"),
            "invalid-field",
            "proposal.next_action"
        },
        {
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[{\"source\":\"Narrative\",\"reference\":\"criterion.done\"}]"),
            "invalid-vocabulary",
            "proposal.evidence_references.item.source"
        },
        {
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[{\"source\":\"CompletionCriterion\",\"reference\":\"free form proof\"}]"),
            "invalid-field",
            "proposal.evidence_references.item.reference"
        },
    };

    [Fact]
    public void Constraint_is_strict_versioned_and_uses_the_closed_parser_vocabularies()
    {
        var constraint = OutcomeProposalConstraint.OutputConstraint;
        var root = constraint.JsonSchema;

        Assert.Equal("hive_outcome_proposal_v2", constraint.SchemaName);
        Assert.Equal(2, constraint.SchemaVersion);
        Assert.Equal(
            [AiOutputConstraintMode.JsonObject, AiOutputConstraintMode.Text],
            constraint.AllowedFallbackModes);
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            2,
            root.GetProperty("properties")
                .GetProperty("schema_version")
                .GetProperty("const")
                .GetInt32());

        var proposal = root.GetProperty("properties").GetProperty("proposal");
        var branches = proposal.GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal(OutcomeProposedIntentContract.WireValues.Length, branches.Length);
        Assert.All(
            branches,
            branch =>
            {
                Assert.False(branch.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(
                    OutcomeProposalConstraint.ProposalRequiredFields,
                    Strings(branch.GetProperty("required")));
                var evidenceItem = branch.GetProperty("properties")
                    .GetProperty("evidence_references")
                    .GetProperty("items");
                Assert.False(evidenceItem.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(
                    OutcomeEvidenceSourceContract.WireValues,
                    Strings(evidenceItem.GetProperty("properties")
                        .GetProperty("source")
                        .GetProperty("enum")));
            });

        var branchIntents = branches
            .Select(branch => branch.GetProperty("properties")
                .GetProperty("proposed_intent")
                .GetProperty("const")
                .GetString())
            .ToArray();
        Assert.Equal(OutcomeProposedIntentContract.WireValues, branchIntents);

        var workStates = branches
            .SelectMany(branch => Strings(branch.GetProperty("properties")
                .GetProperty("work_state")
                .GetProperty("enum")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            OutcomeWorkStateContract.WireValues.OrderBy(value => value, StringComparer.Ordinal),
            workStates.OrderBy(value => value, StringComparer.Ordinal));

        var interventions = branches
            .SelectMany(branch => Strings(branch.GetProperty("properties")
                .GetProperty("required_intervention")
                .GetProperty("enum")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            OutcomeRequiredInterventionContract.WireValues.OrderBy(value => value, StringComparer.Ordinal),
            interventions.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void Contextual_constraint_allows_only_exact_bounded_directive_input_references()
    {
        var evidenceContext = new OutcomeProposalEvidenceContext(
        [
            "directive.objective",
            "directive.context",
        ]);
        var constraint = OutcomeProposalConstraint.CreateOutputConstraint(evidenceContext);

        var branches = constraint.JsonSchema
            .GetProperty("properties")
            .GetProperty("proposal")
            .GetProperty("anyOf")
            .EnumerateArray()
            .ToArray();
        Assert.All(
            branches,
            branch =>
            {
                var evidenceProperties = branch.GetProperty("properties")
                    .GetProperty("evidence_references")
                    .GetProperty("items")
                    .GetProperty("properties");
                Assert.Equal(
                    ["DirectiveInput"],
                    Strings(evidenceProperties.GetProperty("source").GetProperty("enum")));
                Assert.Equal(
                    ["directive.context", "directive.objective"],
                    Strings(evidenceProperties.GetProperty("reference").GetProperty("enum")));
            });
    }

    [Fact]
    public void Contextual_parser_rejects_disallowed_sources_and_ungrounded_references()
    {
        var evidenceContext = new OutcomeProposalEvidenceContext(
        [
            "directive.objective",
            "directive.context",
        ]);
        var allowed = OutcomeProposalParser.Parse(
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[{\"source\":\"DirectiveInput\",\"reference\":\"directive.context\"}]"),
            evidenceContext);
        var disallowedSource = OutcomeProposalParser.Parse(
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[{\"source\":\"RuntimeFact\",\"reference\":\"directive.context\"}]"),
            evidenceContext);
        var ungroundedReference = OutcomeProposalParser.Parse(
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[{\"source\":\"DirectiveInput\",\"reference\":\"directive.missing\"}]"),
            evidenceContext);

        AssertSuccess(allowed);
        AssertFailure(
            disallowedSource,
            "invalid-vocabulary",
            "proposal.evidence_references.item.source");
        AssertFailure(
            ungroundedReference,
            "invalid-field",
            "proposal.evidence_references.item.reference");
    }

    [Fact]
    public void Contextual_parser_reports_missing_required_report_evidence_at_the_evidence_path()
    {
        var result = OutcomeProposalParser.Parse(
            Envelope(
                "Report.Done",
                "Completed",
                "None",
                "[]",
                null,
                "[]"),
            new OutcomeProposalEvidenceContext(["directive.context"]));

        AssertFailure(
            result,
            "invalid-field",
            "proposal.evidence_references");
    }

    [Theory]
    [MemberData(nameof(ValidProposals))]
    public void Every_constraint_branch_parses_to_the_matching_closed_intent(
        string output,
        OutcomeProposedIntent expectedIntent)
    {
        var result = OutcomeProposalParser.Parse(output);

        AssertSuccess(result);
        Assert.Equal(expectedIntent, result.Proposal!.ProposedIntent);
    }

    [Fact]
    public void Parser_normalizes_only_outer_next_action_whitespace()
    {
        var result = OutcomeProposalParser.Parse(Envelope(
            "ContinueWork",
            "InProgress",
            "None",
            "[]",
            "\"  First line.\\n  Second  line.  \"",
            "[]"));

        AssertSuccess(result);
        Assert.Equal("First line.\n  Second  line.", result.Proposal!.NextAction);
    }

    [Theory]
    [MemberData(nameof(InvalidOutputs))]
    public void Invalid_output_fails_with_minimized_closed_diagnostics(
        string output,
        string expectedCode,
        string expectedPath)
    {
        var result = OutcomeProposalParser.Parse(output);

        AssertFailure(result, expectedCode, expectedPath);
    }

    [Fact]
    public void Parser_rejects_unknown_reasoning_and_payload_fields()
    {
        const string output = """
            {
              "schema_version": 2,
              "proposal": {
                "proposed_intent": "ContinueWork",
                "work_state": "InProgress",
                "required_intervention": "None",
                "blockers": [],
                "next_action": "Continue the authorized work.",
                "evidence_references": [],
                "reasoning": "Hidden rationale",
                "report_body": "This must not be materialized yet."
              }
            }
            """;

        var result = OutcomeProposalParser.Parse(output);

        AssertFailure(result, "unknown-field", "proposal");
        Assert.DoesNotContain(result.Errors, error => error.Path.Contains("reasoning"));
        Assert.DoesNotContain(result.Errors, error => error.Path.Contains("Hidden"));
    }

    [Theory]
    [InlineData("Report.Progress", "InProgress", "None", "[]", null, "[]")]
    [InlineData("Report.Done", "Completed", "None", "[]", "\"More work.\"", "[{\"source\":\"RuntimeFact\",\"reference\":\"done\"}]")]
    [InlineData("Escalation", "Blocked", "HumanApproval", "[\"HumanApproval\"]", null, "[]")]
    [InlineData("ApprovalRequired", "Blocked", "HumanApproval", "[\"AuthorityBoundary\"]", null, "[]")]
    [InlineData("Directive", "InProgress", "Delegation", "[\"Routing\"]", "\"Delegate.\"", "[]")]
    public void Parser_rejects_contradictory_combinations(
        string intent,
        string workState,
        string intervention,
        string blockers,
        string? nextAction,
        string evidence)
    {
        var result = OutcomeProposalParser.Parse(Envelope(
            intent,
            workState,
            intervention,
            blockers,
            nextAction,
            evidence));

        AssertFailure(result, "contradictory-combination", "proposal");
    }

    [Fact]
    public void Parser_rejects_duplicate_fields_blockers_and_evidence_references()
    {
        var duplicateField = OutcomeProposalParser.Parse(
            """
            {
              "schema_version": 2,
              "schema_version": 2,
              "proposal": {
                "proposed_intent": "ContinueWork",
                "work_state": "InProgress",
                "required_intervention": "None",
                "blockers": [],
                "next_action": "Continue.",
                "evidence_references": []
              }
            }
            """);
        var duplicateBlocker = OutcomeProposalParser.Parse(Envelope(
            "Escalation",
            "Blocked",
            "SuperiorDecision",
            "[\"Budget\",\"Budget\"]",
            null,
            "[]"));
        var duplicateEvidence = OutcomeProposalParser.Parse(Envelope(
            "Report.Done",
            "Completed",
            "None",
            "[]",
            null,
            "[{\"source\":\"RuntimeFact\",\"reference\":\"done\"},{\"source\":\"RuntimeFact\",\"reference\":\"done\"}]"));

        AssertFailure(duplicateField, "duplicate-field", "$");
        AssertFailure(duplicateBlocker, "invalid-field", "proposal.blockers");
        AssertFailure(duplicateEvidence, "invalid-field", "proposal.evidence_references");
    }

    [Fact]
    public void Diagnostic_contract_is_closed_versioned_and_deterministically_ordered()
    {
        Assert.Equal(1, OutcomeProposalParseDiagnosticContract.Version);
        Assert.Equal(
            OutcomeProposalParseDiagnosticContract.Codes.OrderBy(code => code, StringComparer.Ordinal),
            OutcomeProposalParseDiagnosticContract.Codes);
        Assert.Equal(
            OutcomeProposalParseDiagnosticContract.Paths.OrderBy(path => path, StringComparer.Ordinal),
            OutcomeProposalParseDiagnosticContract.Paths);
        Assert.Throws<ArgumentException>(() =>
            new OutcomeProposalParseError("dynamic-code", "$"));
        Assert.Throws<ArgumentException>(() =>
            new OutcomeProposalParseError("invalid-field", "proposal.dynamic"));
    }

    [Fact]
    public void Constraint_contains_no_function_provider_model_or_reasoning_terms()
    {
        var schema = OutcomeProposalConstraint.OutputConstraint.JsonSchema.GetRawText();

        Assert.All(
            new[] { "triage", "bug", "provider", "openai", "gpt", "reasoning", "rationale", "analysis" },
            term => Assert.DoesNotContain(term, schema, StringComparison.OrdinalIgnoreCase));
    }

    private static string Envelope(
        string intent,
        string workState,
        string intervention,
        string blockers,
        string? nextAction,
        string evidence)
    {
        var nextActionValue = nextAction ?? "null";
        return $$"""
            {
              "schema_version": 2,
              "proposal": {
                "proposed_intent": "{{intent}}",
                "work_state": "{{workState}}",
                "required_intervention": "{{intervention}}",
                "blockers": {{blockers}},
                "next_action": {{nextActionValue}},
                "evidence_references": {{evidence}}
              }
            }
            """;
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static void AssertSuccess(OutcomeProposalParseResult result)
    {
        Assert.True(result.IsSuccess, FormatErrors(result));
        Assert.False(result.IsFailure);
        Assert.NotNull(result.Proposal);
        Assert.Empty(result.Errors);
    }

    private static void AssertFailure(
        OutcomeProposalParseResult result,
        string expectedCode,
        string expectedPath)
    {
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Proposal);
        Assert.Contains(
            result.Errors,
            error => error.Code == expectedCode && error.Path == expectedPath);
    }

    private static string FormatErrors(OutcomeProposalParseResult result) =>
        string.Join(Environment.NewLine, result.Errors.Select(error => $"{error.Path}: {error.Code}"));
}
