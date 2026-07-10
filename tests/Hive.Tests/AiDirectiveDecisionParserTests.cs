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
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"},\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":[\"Ask lead.\"]}}", "payload-ambiguous", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":[\"Ask lead.\"]}}", "payload-intent-mismatch", "$" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"message_id\":\"model-made-id\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}", "unknown-field", "message_id" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\",\"directive_id\":\"model-made-id\"}}", "unknown-field", "report.directive_id" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Blocked\",\"body\":\"Working.\"}}", "invalid-field", "report.kind" },
        { "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\" \"}}", "invalid-field", "report.body" },
        { "{\"schema_version\":1,\"intent\":\"Escalation\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":null}}", "invalid-field", "escalation.options_considered" },
        { "{\"schema_version\":1,\"intent\":\"Escalation\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":\"Ask lead.\"}}", "invalid-field", "escalation.options_considered" },
        { "{\"schema_version\":1,\"intent\":\"Escalation\",\"escalation\":{\"issue\":\"Need help.\",\"context\":\"Blocked.\",\"options_considered\":[\"Ask lead.\",\" \"]}}", "invalid-field", "escalation.options_considered[1]" },
        { "{\"schema_version\":1,\"intent\":\"Directive\",\"directive\":{\"target_position_id\":\" \",\"objective\":\"Investigate.\",\"context\":\"Use logs.\"}}", "invalid-field", "directive.target_position_id" },
    };

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

        AssertFailure(result, "unknown-field", "thread_id");
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
