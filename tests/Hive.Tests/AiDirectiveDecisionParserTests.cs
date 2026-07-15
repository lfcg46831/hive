using Hive.Actors.Positions;
using Hive.Domain.Governance;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class AiDirectiveDecisionParserTests
{
    public static TheoryData<string?, string, string> InvalidOutputs => new()
    {
        { null, "empty-response", "$" },
        { "", "empty-response", "$" },
        { "   ", "empty-response", "$" },
        { "```json\n{\"schema_version\":1,\"intent\":\"Report\"}\n```", "invalid-json", "$" },
        { "{", "invalid-json", "$" },
        { "[]", "top-level-object-required", "$" },
        { "{\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "required-field", "schema_version" },
        { "{\"schema_version\":\"1\",\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "invalid-schema-version", "schema_version" },
        { "{\"schema_version\":2,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "invalid-schema-version", "schema_version" },
        { "{\"schema_version\":1,\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "required-field", "intent" },
        { "{\"schema_version\":1,\"intent\":\"Memo\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "invalid-intent", "intent" },
        { "{\"schema_version\":1,\"intent\":\"Report\"}", "payload-required", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":null,\"escalation\":null,\"directive\":null}", "payload-required", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"},\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":[\"Ask lead.\"]}}", "payload-ambiguous", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":[\"Ask lead.\"]}}", "payload-intent-mismatch", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"message_id\":\"model-made-id\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "unknown-field", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\",\"directive_id\":\"model-made-id\"}}", "unknown-field", "report" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Blocked\",\"body\":\"Working.\"}}", "invalid-field", "report.kind" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\" \"}}", "invalid-field", "report.body" },
        { "{\"schema_version\":1,\"intent\":\"Escalation\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":null}}", "invalid-field", "escalation.options_considered" },
        { "{\"schema_version\":1,\"intent\":\"Escalation\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":\"Ask lead.\"}}", "invalid-field", "escalation.options_considered" },
        { "{\"schema_version\":1,\"intent\":\"Escalation\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":[\"Ask lead.\",\" \"]}}", "invalid-field", "escalation.options_considered.item" },
        { "{\"schema_version\":1,\"intent\":\"Directive\",\"directive\":{\"target_position_id\":\" \",\"objective\":\"Investigate.\",\"context\":\"Use logs.\"}}", "invalid-field", "directive.target_position_id" },
    };

    [Fact]
    public void Canonical_nested_union_parses_the_single_intent_payload_pair()
    {
        const string output = """
            {
              "schema_version": 1,
              "acting_under": "delivery.bug-triage",
              "decision": {
                "intent": "Report",
                "report": {
                  "kind": "Done",
                  "body": "Triage completed."
                }
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(
            output,
            [AuthorityKey.From("delivery.bug-triage")]);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveReportDecision>(result.Decision);
        Assert.Equal("Triage completed.", decision.Body);
        Assert.Equal(ActingUnderDeclarationState.Declared, decision.ActingUnder.State);
    }

    [Fact]
    public void Free_text_normalizes_only_outer_unicode_whitespace_and_preserves_internal_text()
    {
        const string output = """
            {
              "schema_version": 1,
              "acting_under": "delivery.bug-triage",
              "decision": {
                "intent": "Report",
                "report": {
                  "kind": "Progress",
                  "body": "   First line.\n  Second  line.  \n"
                }
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(output);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveReportDecision>(result.Decision);
        Assert.Equal("First line.\n  Second  line.", decision.Body);
    }

    [Fact]
    public void Canonical_union_escalation_and_directive_branches_are_parser_compatible()
    {
        const string escalation = """
            {"schema_version":1,"acting_under":"delivery.bug-triage","decision":{"intent":"Escalation","escalation":{"issue":" Need approval. ","context":" Blocked. ","options_considered":[" Ask lead. "]}}}
            """;
        const string directive = """
            {"schema_version":1,"acting_under":"delivery.bug-triage","decision":{"intent":"Directive","directive":{"target_position_id":" engineer ","objective":" Investigate. ","context":" Use logs. "}}}
            """;

        var escalationResult = AiDirectiveDecisionParser.Parse(escalation);
        var directiveResult = AiDirectiveDecisionParser.Parse(directive);

        AssertSuccess(escalationResult);
        AssertSuccess(directiveResult);
        Assert.Equal(
            "Need approval.",
            Assert.IsType<AiDirectiveEscalationDecision>(escalationResult.Decision).Issue);
        Assert.Equal(
            "engineer",
            Assert.IsType<AiDirectiveChildDirectiveDecision>(directiveResult.Decision)
                .TargetPositionId.Value);
    }

    [Fact]
    public void Canonical_union_rejects_unicode_whitespace_only_after_local_normalization()
    {
        const string output = """
            {"schema_version":1,"acting_under":"delivery.bug-triage","decision":{"intent":"Report","report":{"kind":"Done","body":"  　"}}}
            """;

        var result = AiDirectiveDecisionParser.Parse(output);

        AssertFailure(result, "invalid-field", "decision.report.body");
    }

    [Fact]
    public void Parse_diagnostic_contract_is_closed_versioned_and_deterministic()
    {
        Assert.Equal(1, AiDirectiveDecisionParseDiagnosticContract.Version);
        Assert.Equal(
            AiDirectiveDecisionParseDiagnosticContract.Codes
                .OrderBy(value => value, StringComparer.Ordinal),
            AiDirectiveDecisionParseDiagnosticContract.Codes);
        Assert.Equal(
            AiDirectiveDecisionParseDiagnosticContract.Paths
                .OrderBy(value => value, StringComparer.Ordinal),
            AiDirectiveDecisionParseDiagnosticContract.Paths);
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveDecisionParseError("invalid-field", "decision.dynamic-value"));
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveDecisionParseError("dynamic-code", "$"));
    }

    [Fact]
    public void Valid_report_output_parses_into_report_decision()
    {
        const string output = """
            {
              "schema_version": 1,
              "intent": "Report",
              "report": {
                "kind": "Progress",
                "body": "Investigation is underway."
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(output);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveReportDecision>(result.Decision);
        Assert.Equal(AiDirectiveDecisionIntent.Report, decision.Intent);
        Assert.Equal(ReportKind.Progress, decision.Kind);
        Assert.Equal("Investigation is underway.", decision.Body);
        Assert.Equal(ActingUnderDeclarationState.Missing, decision.ActingUnder.State);
        Assert.Equal(ActingUnderDeclaration.MissingCode, decision.ActingUnder.Code);
        Assert.Null(decision.ActingUnder.Key);
    }

    [Fact]
    public void Structured_output_with_null_inactive_payloads_parses_into_selected_decision()
    {
        const string output = """
            {
              "schema_version": 1,
              "intent": "Report",
              "acting_under": "delivery.bug-triage",
              "report": {
                "kind": "Done",
                "body": "Triage completed."
              },
              "escalation": null,
              "directive": null
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(
            output,
            [AuthorityKey.From("delivery.bug-triage")]);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveReportDecision>(result.Decision);
        Assert.Equal(ReportKind.Done, decision.Kind);
        Assert.Equal("Triage completed.", decision.Body);
        Assert.Equal(ActingUnderDeclarationState.Declared, decision.ActingUnder.State);
    }

    [Fact]
    public void Valid_acting_under_is_bound_to_the_positions_closed_vocabulary()
    {
        const string output = """
            {
              "schema_version": 1,
              "intent": "Report",
              "acting_under": "delivery.bug-triage",
              "report": {
                "kind": "Progress",
                "body": "Investigation is underway."
              }
            }
            """;
        var allowed = AuthorityKey.From("delivery.bug-triage");

        var result = AiDirectiveDecisionParser.Parse(output, [allowed]);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveReportDecision>(result.Decision);
        Assert.Equal(ActingUnderDeclarationState.Declared, decision.ActingUnder.State);
        Assert.Equal(ActingUnderDeclaration.DeclaredCode, decision.ActingUnder.Code);
        Assert.Same(allowed, decision.ActingUnder.Key);
    }

    [Theory]
    [InlineData("\"finance.commitments\"")]
    [InlineData("\"not-namespaced\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Invalid_acting_under_is_non_declared_without_rejecting_the_payload(
        string actingUnderJson)
    {
        var output = $$"""
            {
              "schema_version": 1,
              "intent": "Report",
              "acting_under": {{actingUnderJson}},
              "report": {
                "kind": "Progress",
                "body": "Investigation is underway."
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(
            output,
            [AuthorityKey.From("delivery.bug-triage")]);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveReportDecision>(result.Decision);
        Assert.Equal(ActingUnderDeclarationState.Invalid, decision.ActingUnder.State);
        Assert.Equal(ActingUnderDeclaration.InvalidCode, decision.ActingUnder.Code);
        Assert.Null(decision.ActingUnder.Key);
        Assert.DoesNotContain("finance.commitments", decision.ActingUnder.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_escalation_output_parses_into_escalation_decision()
    {
        const string output = """
            {
              "schema_version": 1,
              "intent": "Escalation",
              "escalation": {
                "issue": "Need approval.",
                "context": "The directive asks for a production release.",
                "options_considered": [
                  "Ask the release owner.",
                  "Wait for the approval policy."
                ]
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(output);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveEscalationDecision>(result.Decision);
        Assert.Equal(AiDirectiveDecisionIntent.Escalation, decision.Intent);
        Assert.Equal("Need approval.", decision.Issue);
        Assert.Equal("The directive asks for a production release.", decision.Context);
        Assert.Equal(
            ["Ask the release owner.", "Wait for the approval policy."],
            decision.OptionsConsidered);
    }

    [Fact]
    public void Valid_directive_output_parses_into_child_directive_decision()
    {
        const string output = """
            {
              "schema_version": 1,
              "intent": "Directive",
              "directive": {
                "target_position_id": "engineer",
                "objective": "Investigate checkout regression.",
                "context": "Focus on payment callback failures."
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(output);

        AssertSuccess(result);
        var decision = Assert.IsType<AiDirectiveChildDirectiveDecision>(result.Decision);
        Assert.Equal(AiDirectiveDecisionIntent.Directive, decision.Intent);
        Assert.Equal("engineer", decision.TargetPositionId.Value);
        Assert.Equal("Investigate checkout regression.", decision.Objective);
        Assert.Equal("Focus on payment callback failures.", decision.Context);
    }

    [Theory]
    [MemberData(nameof(InvalidOutputs))]
    public void Invalid_output_fails_without_throwing_and_reports_stable_error(
        string? output,
        string expectedCode,
        string expectedPath)
    {
        var result = AiDirectiveDecisionParser.Parse(output);

        AssertFailure(result, expectedCode, expectedPath);
    }

    [Fact]
    public void Failure_result_has_no_decision_and_structured_errors()
    {
        const string output = """
            {
              "schema_version": 1,
              "intent": "Report",
              "thread_id": "model-made-thread",
              "report": {
                "kind": "Progress",
                "body": "Working."
              }
            }
            """;

        var result = AiDirectiveDecisionParser.Parse(output);

        AssertFailure(result, "unknown-field", "$");
        Assert.Null(result.Decision);
        Assert.All(
            result.Errors,
            error =>
            {
                Assert.False(string.IsNullOrWhiteSpace(error.Code));
                Assert.False(string.IsNullOrWhiteSpace(error.Path));
            });
    }

    private static void AssertSuccess(AiDirectiveDecisionParseResult result)
    {
        Assert.True(result.IsSuccess, FormatErrors(result));
        Assert.False(result.IsFailure);
        Assert.NotNull(result.Decision);
        Assert.Empty(result.Errors);
    }

    private static void AssertFailure(
        AiDirectiveDecisionParseResult result,
        string expectedCode,
        string expectedPath)
    {
        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Null(result.Decision);
        Assert.Contains(
            result.Errors,
            error => error.Code == expectedCode && error.Path == expectedPath);
    }

    private static string FormatErrors(AiDirectiveDecisionParseResult result) =>
        string.Join(
            Environment.NewLine,
            result.Errors.Select(error => $"{error.Path}: {error.Code}"));
}
