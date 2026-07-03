using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class AiDirectiveDecisionContractTests
{
    [Fact]
    public void Schema_declares_version_intents_and_required_payload_fields_only()
    {
        Assert.Equal(1, AiDirectiveDecisionSchema.SchemaVersion);
        Assert.Equal("schema_version", AiDirectiveDecisionSchema.SchemaVersionProperty);
        Assert.Equal("intent", AiDirectiveDecisionSchema.IntentProperty);
        Assert.Equal(
            ["Report", "Escalation", "Directive"],
            AiDirectiveDecisionSchema.AllowedIntents.ToArray());

        Assert.Equal("report", AiDirectiveDecisionSchema.ReportPayloadProperty);
        Assert.Equal(["kind", "body"], AiDirectiveDecisionSchema.ReportRequiredFields.ToArray());
        Assert.Equal("escalation", AiDirectiveDecisionSchema.EscalationPayloadProperty);
        Assert.Equal(
            ["issue", "context", "options_considered"],
            AiDirectiveDecisionSchema.EscalationRequiredFields.ToArray());
        Assert.Equal("directive", AiDirectiveDecisionSchema.DirectivePayloadProperty);
        Assert.Equal(
            ["target_position_id", "objective", "context"],
            AiDirectiveDecisionSchema.DirectiveRequiredFields.ToArray());

        var modelWritableFields = AiDirectiveDecisionSchema.ReportRequiredFields
            .Concat(AiDirectiveDecisionSchema.EscalationRequiredFields)
            .Concat(AiDirectiveDecisionSchema.DirectiveRequiredFields)
            .ToArray();
        Assert.DoesNotContain("message_id", modelWritableFields);
        Assert.DoesNotContain("thread_id", modelWritableFields);
        Assert.DoesNotContain("directive_id", modelWritableFields);
        Assert.DoesNotContain("parent_directive_id", modelWritableFields);
        Assert.DoesNotContain("from", modelWritableFields);
        Assert.DoesNotContain("to", modelWritableFields);
    }

    [Theory]
    [InlineData(1, "Report")]
    [InlineData(2, "Escalation")]
    [InlineData(3, "Directive")]
    public void Decision_intents_have_stable_wire_values(
        int rawIntent,
        string wireValue)
    {
        var intent = (AiDirectiveDecisionIntent)rawIntent;

        Assert.Equal(wireValue, AiDirectiveDecisionIntentContract.ToWireValue(intent));
        Assert.Equal(intent, AiDirectiveDecisionIntentContract.ParseWireValue(wireValue));
        Assert.True(AiDirectiveDecisionIntentContract.TryParseWireValue(wireValue, out var parsed));
        Assert.Equal(intent, parsed);
    }

    [Fact]
    public void Report_decision_preserves_allowed_payload_and_rejects_invalid_values()
    {
        var progress = new AiDirectiveReportDecision(ReportKind.Progress, "Investigation started.");
        var done = new AiDirectiveReportDecision(ReportKind.Done, "Bug triaged.");

        Assert.Equal(AiDirectiveDecisionIntent.Report, progress.Intent);
        Assert.Equal(ReportKind.Progress, progress.Kind);
        Assert.Equal("Investigation started.", progress.Body);
        Assert.Equal(AiDirectiveDecisionIntent.Report, done.Intent);
        Assert.Equal(ReportKind.Done, done.Kind);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AiDirectiveReportDecision((ReportKind)0, "Body."));
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveReportDecision(ReportKind.Progress, " "));
    }

    [Fact]
    public void Escalation_decision_preserves_allowed_payload_and_rejects_invalid_values()
    {
        var decision = new AiDirectiveEscalationDecision(
            "Need a product decision.",
            "The directive asks for production release approval.",
            ["Ask release owner", "Wait for approval policy"]);

        Assert.Equal(AiDirectiveDecisionIntent.Escalation, decision.Intent);
        Assert.Equal("Need a product decision.", decision.Issue);
        Assert.Equal("The directive asks for production release approval.", decision.Context);
        Assert.Equal(["Ask release owner", "Wait for approval policy"], decision.OptionsConsidered);

        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveEscalationDecision(" ", "Context.", ["Option."]));
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveEscalationDecision("Issue.", " ", ["Option."]));
        Assert.Throws<ArgumentNullException>(() =>
            new AiDirectiveEscalationDecision("Issue.", "Context.", null!));
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveEscalationDecision("Issue.", "Context.", ["Option.", " "]));
    }

    [Fact]
    public void Directive_decision_preserves_allowed_payload_and_rejects_invalid_values()
    {
        var target = PositionId.From("engineer");
        var decision = new AiDirectiveChildDirectiveDecision(
            target,
            "Investigate checkout regression.",
            "Focus on payment callback failures.");

        Assert.Equal(AiDirectiveDecisionIntent.Directive, decision.Intent);
        Assert.Equal(target, decision.TargetPositionId);
        Assert.Equal("Investigate checkout regression.", decision.Objective);
        Assert.Equal("Focus on payment callback failures.", decision.Context);

        Assert.Throws<ArgumentNullException>(() =>
            new AiDirectiveChildDirectiveDecision(null!, "Objective.", "Context."));
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveChildDirectiveDecision(target, " ", "Context."));
        Assert.Throws<ArgumentException>(() =>
            new AiDirectiveChildDirectiveDecision(target, "Objective.", " "));
    }

    [Fact]
    public void Decision_types_stay_internal_to_ai_agent_processing()
    {
        Assert.True(typeof(AiDirectiveDecision).IsAssignableFrom(typeof(AiDirectiveReportDecision)));
        Assert.True(typeof(AiDirectiveDecision).IsAssignableFrom(typeof(AiDirectiveEscalationDecision)));
        Assert.True(typeof(AiDirectiveDecision).IsAssignableFrom(typeof(AiDirectiveChildDirectiveDecision)));
        Assert.False(typeof(OrgMessage).IsAssignableFrom(typeof(AiDirectiveDecision)));
        Assert.Equal("Hive.Actors", typeof(AiDirectiveDecision).Assembly.GetName().Name);
    }
}
