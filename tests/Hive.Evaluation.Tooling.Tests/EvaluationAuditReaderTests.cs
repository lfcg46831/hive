using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationAuditReaderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(
        "Failed",
        "failed",
        "provider-rejected",
        "ai-gateway-failure",
        "{\"terminalCode\":\"ai-gateway-failure\"}",
        "provider-rejected")]
    [InlineData(
        "Rejected",
        "rejected",
        "policy-rejected",
        "policy-rejected",
        "{}",
        "policy-rejected")]
    public void Projects_terminal_failed_or_rejected_decision_with_gateway_cost(
        string persistedOutcome,
        string expectedOutcome,
        string gatewayReasonCode,
        string decisionReasonCode,
        string payload,
        string expectedTerminalCode)
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(-120_000, "DirectiveCreated", "Accepted"),
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                persistedOutcome,
                reasonCode: gatewayReasonCode,
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 125,
                inputTokens: 20,
                outputTokens: 4,
                totalTokens: 24,
                tokensEstimated: false,
                costAmount: 0.0123m,
                costCurrency: "USD",
                costEstimated: true,
                payload: "{\"outputConstraintMode\":\"json-schema\",\"costStatus\":\"estimated\",\"pricingVersion\":\"pricing-v1\",\"pricingTokenUnit\":\"1000000\",\"inputPricePerTokenUnit\":\"0.25\",\"outputPricePerTokenUnit\":\"2\"}"),
            Row(
                2,
                "AgentDecided",
                persistedOutcome,
                reasonCode: decisionReasonCode,
                payload: payload),
        ]);

        Assert.NotNull(journey);
        Assert.Equal(expectedOutcome, journey.Outcome);
        Assert.Equal(expectedTerminalCode, journey.TerminalCode);
        Assert.Null(journey.Decision);
        Assert.Equal("openai", journey.ProviderId);
        Assert.Equal("gpt-test", journey.ModelId);
        Assert.Equal("json-schema", journey.OutputConstraintMode);
        Assert.Equal(24, journey.TotalTokens);
        Assert.Equal(0.0123m, journey.CostAmount);
        Assert.Equal("estimated", journey.CostStatus);
        Assert.Equal("pricing-v1", journey.PricingVersion);
        Assert.Equal(1_000_000, journey.PricingTokenUnit);
        Assert.Equal(0.25m, journey.InputPricePerTokenUnit);
        Assert.Equal(2m, journey.OutputPricePerTokenUnit);
        Assert.Equal(2000, journey.JourneyDurationMilliseconds);
    }

    [Fact]
    public void Projects_result_message_with_gateway_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"completed\"}"),
            Row(2, "ResultMessageCreated", "Succeeded", messageType: "Report"),
            Row(3, "GatewayCostRecorded", "Succeeded", providerId: "stub", modelId: "triage"),
        ]);

        Assert.NotNull(journey);
        Assert.Equal("succeeded", journey.Outcome);
        Assert.Equal("completed", journey.TerminalCode);
        Assert.Equal("report", journey.Decision);
        Assert.Equal("cost-unavailable", journey.CostStatus);
        Assert.Equal(3000, journey.JourneyDurationMilliseconds);
    }

    [Fact]
    public void Does_not_project_successful_decision_without_result_message()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"completed\"}"),
        ]);

        Assert.Null(journey);
    }

    [Fact]
    public void Does_not_project_terminal_failure_without_gateway_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(
                1,
                "AgentDecided",
                "Failed",
                reasonCode: "provider-unavailable",
                payload: "{}"),
        ]);

        Assert.Null(journey);
    }

    [Fact]
    public void Projects_provider_timeout_as_terminal_with_unavailable_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                "Failed",
                reasonCode: "timeout",
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 15_000,
                payload: "{\"costStatus\":\"cost-unavailable\",\"isRetryable\":\"True\"}"),
            Row(
                2,
                "AgentDecided",
                "Failed",
                reasonCode: "ai-gateway-failure",
                payload: "{\"terminalCode\":\"ai-gateway-failure\"}"),
        ]);

        Assert.NotNull(journey);
        Assert.Equal("failed", journey.Outcome);
        Assert.Equal("timeout", journey.TerminalCode);
        Assert.Equal("cost-unavailable", journey.CostStatus);
        Assert.Null(journey.InputTokens);
        Assert.Null(journey.OutputTokens);
        Assert.Null(journey.TotalTokens);
        Assert.Null(journey.CostAmount);
    }

    [Fact]
    public void Aggregates_each_inference_and_verifier_call_without_zero_filling_partial_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                "Succeeded",
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 120,
                inputTokens: 20,
                outputTokens: 4,
                totalTokens: 24,
                tokensEstimated: false,
                costAmount: 0.002m,
                costCurrency: "USD",
                costEstimated: true,
                payload: "{\"operation\":\"directive-inference\",\"iteration\":\"1\",\"outputConstraintMode\":\"json-schema\",\"costStatus\":\"estimated\",\"pricingVersion\":\"pricing-v1\",\"pricingTokenUnit\":\"1000000\",\"inputPricePerTokenUnit\":\"0.25\",\"outputPricePerTokenUnit\":\"2\"}"),
            Row(
                2,
                "GatewayCostRecorded",
                "Failed",
                reasonCode: "provider-rejected",
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 30,
                payload: "{\"operation\":\"outcome-verification\",\"iteration\":\"1\",\"outputConstraintMode\":\"json-schema\",\"costStatus\":\"cost-unavailable\",\"finishReason\":\"Length\",\"providerStatusCode\":\"400\",\"requestTimeoutMilliseconds\":\"30000\",\"maxOutputTokens\":\"2048\",\"executionLimitsVersion\":\"1\",\"executionBudgetMilliseconds\":\"90000\",\"perCallTimeoutMilliseconds\":\"60000\"}"),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Escalation"),
        ]);

        Assert.NotNull(journey);
        Assert.Equal("cost-unavailable", journey.CostStatus);
        Assert.Null(journey.InputTokens);
        Assert.Null(journey.TotalTokens);
        Assert.Null(journey.CostAmount);
        Assert.Equal(150, journey.GatewayLatencyMilliseconds);
        var calls = Assert.IsAssignableFrom<IReadOnlyList<EvaluationGatewayCall>>(
            journey.GatewayCalls);
        Assert.Equal(2, calls.Count);
        Assert.Equal("directive-inference", calls[0].Operation);
        Assert.Equal("outcome-verification", calls[1].Operation);
        Assert.Equal(1, calls[0].Iteration);
        Assert.Equal(1, calls[1].Iteration);
        Assert.Equal(0.002m, calls[0].CostAmount);
        Assert.Null(calls[1].CostAmount);
        Assert.Equal("provider-rejected", calls[1].ReasonCode);
        Assert.Equal("Length", calls[1].FinishReason);
        Assert.Equal(400, calls[1].ProviderStatusCode);
        Assert.Equal(30_000d, calls[1].RequestTimeoutMilliseconds);
        Assert.Equal(2048, calls[1].MaxOutputTokens);
        Assert.Equal(1, calls[1].ExecutionLimitsVersion);
        Assert.Equal(90_000d, calls[1].ExecutionBudgetMilliseconds);
        Assert.Equal(60_000d, calls[1].PerCallTimeoutMilliseconds);
    }

    [Fact]
    public void Sums_complete_usage_cost_and_latency_across_gateway_calls()
    {
        var commonPayload =
            "\"outputConstraintMode\":\"json-schema\",\"costStatus\":\"estimated\",\"pricingVersion\":\"pricing-v1\",\"pricingTokenUnit\":\"1000000\",\"inputPricePerTokenUnit\":\"0.25\",\"outputPricePerTokenUnit\":\"2\"";
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                "Succeeded",
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 100,
                inputTokens: 20,
                outputTokens: 4,
                totalTokens: 24,
                tokensEstimated: false,
                costAmount: 0.002m,
                costCurrency: "USD",
                costEstimated: true,
                payload: "{\"operation\":\"directive-inference\",\"iteration\":\"1\"," + commonPayload + "}"),
            Row(
                2,
                "GatewayCostRecorded",
                "Succeeded",
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 50,
                inputTokens: 10,
                outputTokens: 2,
                totalTokens: 12,
                tokensEstimated: false,
                costAmount: 0.001m,
                costCurrency: "USD",
                costEstimated: true,
                payload: "{\"operation\":\"outcome-verification\",\"iteration\":\"1\"," + commonPayload + "}"),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Report"),
        ]);

        Assert.NotNull(journey);
        Assert.Equal(30, journey.InputTokens);
        Assert.Equal(6, journey.OutputTokens);
        Assert.Equal(36, journey.TotalTokens);
        Assert.False(journey.TokensEstimated);
        Assert.Equal(0.003m, journey.CostAmount);
        Assert.Equal("USD", journey.CostCurrency);
        Assert.True(journey.CostEstimated);
        Assert.Equal("estimated", journey.CostStatus);
        Assert.Equal(150, journey.GatewayLatencyMilliseconds);
        Assert.Equal(2, journey.GatewayCalls!.Count);
    }

    [Fact]
    public void Projects_only_closed_versioned_invalid_output_diagnostics()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                "Succeeded",
                providerId: "openai",
                modelId: "gpt-test"),
            Row(
                2,
                "AgentDecided",
                "Failed",
                reasonCode: "ai-output-invalid",
                payload: "{\"terminalCode\":\"ai-output-invalid\",\"parseErrorContractVersion\":\"1\",\"parseErrorCount\":\"2\",\"parseError.0.path\":\"decision\",\"parseError.0.code\":\"payload-ambiguous\",\"parseError.1.path\":\"decision.report.body\",\"parseError.1.code\":\"invalid-field\"}"),
        ]);

        Assert.NotNull(journey);
        var diagnostics = Assert.IsType<EvaluationInvalidOutputDiagnostics>(
            journey.InvalidOutputDiagnostics);
        Assert.Equal(1, diagnostics.ContractVersion);
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(
            [
                new EvaluationInvalidOutputDiagnostic("decision", "payload-ambiguous"),
                new EvaluationInvalidOutputDiagnostic("decision.report.body", "invalid-field"),
            ],
            diagnostics.Errors);
    }

    [Fact]
    public void Projects_v2_outcome_proposal_parse_diagnostics()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(
                2,
                "AgentDecided",
                "Failed",
                reasonCode: "ai-output-invalid",
                payload: "{\"parseErrorContractVersion\":\"2\",\"parseErrorCount\":\"1\",\"parseError.0.path\":\"outcome_proposal.proposal.proposed_intent\",\"parseError.0.code\":\"contradictory-combination\"}"),
        ]);

        var diagnostics = Assert.IsType<EvaluationInvalidOutputDiagnostics>(
            journey!.InvalidOutputDiagnostics);
        Assert.Equal(2, diagnostics.ContractVersion);
        Assert.Equal(
            new EvaluationInvalidOutputDiagnostic(
                "outcome_proposal.proposal.proposed_intent",
                "contradictory-combination"),
            Assert.Single(diagnostics.Errors));
    }

    [Fact]
    public void Rejects_dynamic_or_unversioned_parse_diagnostics_from_the_read_model()
    {
        var rows = new[]
        {
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(
                2,
                "AgentDecided",
                "Failed",
                reasonCode: "ai-output-invalid",
                payload: "{\"parseErrorContractVersion\":\"1\",\"parseErrorCount\":\"1\",\"parseError.0.path\":\"decision.rejected-secret\",\"parseError.0.code\":\"invalid-field\"}"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EvaluationJourneyProjector.TryProject(rows));

        Assert.Equal(
            "Evaluation parse diagnostic is outside the closed contract.",
            exception.Message);
        Assert.DoesNotContain("rejected-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_minimized_outcome_resolution_without_provider_output()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded", providerId: "openai", modelId: "main"),
            Row(
                2,
                "OutcomeResolved",
                "Succeeded",
                messageType: "Escalation",
                providerId: "openai",
                modelId: "main",
                latencyMilliseconds: 37,
                inputTokens: 20,
                outputTokens: 4,
                totalTokens: 24,
                costAmount: 0.001m,
                costCurrency: "USD",
                payload: "{\"mode\":\"enforcement\",\"iteration\":\"1\",\"proposedIntent\":\"Report.Done\",\"workState\":\"Completed\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-v1/registry-3\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"true\",\"verifierStatus\":\"Classified\",\"verifierClassification\":\"Undetermined\",\"semanticCompletionCandidate\":\"true\",\"semanticCompletionIneligibilityReasonCount\":\"0\",\"deadlineRemainingMilliseconds\":\"5000\",\"reasonCount\":\"1\",\"reason.0\":\"verifier-disagreement\",\"diagnosticCount\":\"0\",\"redactions\":\"prompt,provider-output,verification-artifact\"}"),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Escalation"),
        ]);

        Assert.NotNull(journey);
        var resolution = Assert.IsType<EvaluationOutcomeResolution>(journey.OutcomeResolution);
        Assert.Equal("enforcement", resolution.Mode);
        Assert.Equal("Report.Done", resolution.ProposedIntent);
        Assert.Equal("Escalation", resolution.ResolvedOutcome);
        Assert.Equal(["verifier-disagreement"], resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
        Assert.True(resolution.VerifierInvoked);
        Assert.Equal("Classified", resolution.VerifierStatus);
        Assert.Equal("Undetermined", resolution.VerifierClassification);
        Assert.True(resolution.SemanticCompletionCandidate);
        Assert.Empty(resolution.SemanticCompletionIneligibilityReasons!);
        Assert.Equal(5000, resolution.DeadlineRemainingMilliseconds);
        Assert.Empty(resolution.Diagnostics);
        Assert.Equal(37, resolution.LatencyMilliseconds);
        Assert.Equal(24, resolution.TotalTokens);
        Assert.Single(journey.OutcomeResolutionSteps!);
        Assert.DoesNotContain("provider-output", resolution.Reasons);
    }

    [Fact]
    public void Projects_each_minimized_outcome_resolution_step_and_keeps_the_last_terminal_view()
    {
        const string first =
            "{\"mode\":\"enforcement\",\"iteration\":\"1\",\"proposedIntent\":\"Report.Progress\",\"workState\":\"InProgress\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"ContinueWork\",\"policyVersion\":\"outcome-policy-v1\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"false\",\"semanticCompletionCandidate\":\"false\",\"semanticCompletionIneligibilityReasonCount\":\"3\",\"semanticCompletionIneligibilityReason.0\":\"proposal-intent-not-report-done\",\"semanticCompletionIneligibilityReason.1\":\"work-state-not-completed\",\"semanticCompletionIneligibilityReason.2\":\"next-action-present\",\"deadlineRemainingMilliseconds\":\"12000\",\"reasonCount\":\"1\",\"reason.0\":\"autonomous-action-available\",\"diagnosticCount\":\"0\"}";
        const string second =
            "{\"mode\":\"enforcement\",\"iteration\":\"2\",\"proposedIntent\":\"Report.Done\",\"workState\":\"Completed\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-v1\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"true\",\"verifierStatus\":\"Classified\",\"verifierClassification\":\"Undetermined\",\"semanticCompletionCandidate\":\"false\",\"semanticCompletionIneligibilityReasonCount\":\"2\",\"semanticCompletionIneligibilityReason.0\":\"evidence-source-not-directive-input\",\"semanticCompletionIneligibilityReason.1\":\"evidence-reference-not-in-context\",\"deadlineRemainingMilliseconds\":\"0\",\"reasonCount\":\"1\",\"reason.0\":\"verifier-disagreement\",\"diagnosticCount\":\"0\"}";
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "OutcomeResolved", "Succeeded", payload: first),
            Row(3, "OutcomeResolved", "Succeeded", payload: second),
            Row(4, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(5, "ResultMessageCreated", "Succeeded", messageType: "Escalation"),
        ]);

        Assert.NotNull(journey);
        var steps = Assert.IsAssignableFrom<IReadOnlyList<EvaluationOutcomeResolution>>(
            journey.OutcomeResolutionSteps);
        Assert.Equal([1, 2], steps.Select(step => step.Iteration));
        Assert.Equal([12000L, 0L], steps.Select(step => step.DeadlineRemainingMilliseconds));
        Assert.Equal("ContinueWork", steps[0].ResolvedOutcome);
        Assert.Equal("Escalation", journey.OutcomeResolution!.ResolvedOutcome);
        Assert.Same(steps[1], journey.OutcomeResolution);
    }

    [Fact]
    public void Historical_resolution_without_eligibility_or_deadline_diagnostics_remains_readable()
    {
        const string historical =
            "{\"mode\":\"shadow\",\"iteration\":\"1\",\"proposedIntent\":\"Escalation\",\"workState\":\"Blocked\",\"requiredIntervention\":\"SuperiorDecision\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-v1\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"false\",\"verifierInvoked\":\"false\",\"reasonCount\":\"1\",\"reason.0\":\"proposal-escalation\",\"diagnosticCount\":\"0\"}";
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "OutcomeResolved", "Succeeded", payload: historical),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Escalation"),
        ]);

        var resolution = Assert.IsType<EvaluationOutcomeResolution>(
            journey!.OutcomeResolution);
        Assert.Null(resolution.SemanticCompletionCandidate);
        Assert.Null(resolution.SemanticCompletionIneligibilityReasons);
        Assert.Null(resolution.DeadlineRemainingMilliseconds);
        Assert.Single(journey.OutcomeResolutionSteps!);
    }

    [Theory]
    [InlineData(
        "false",
        "1",
        "hidden-model-value",
        "Semantic-completion ineligibility reason is outside the closed contract.")]
    [InlineData(
        "true",
        "1",
        "evidence-reference-not-in-context",
        "Semantic-completion eligibility audit values are inconsistent.")]
    public void Rejects_invalid_or_inconsistent_semantic_completion_diagnostics(
        string candidate,
        string reasonCount,
        string reason,
        string expectedMessage)
    {
        var payload =
            "{\"mode\":\"enforcement\",\"iteration\":\"1\",\"proposedIntent\":\"Report.Done\",\"workState\":\"Completed\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-v1\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"true\",\"semanticCompletionCandidate\":\"" +
            candidate +
            "\",\"semanticCompletionIneligibilityReasonCount\":\"" +
            reasonCount +
            "\",\"semanticCompletionIneligibilityReason.0\":\"" +
            reason +
            "\",\"reasonCount\":\"1\",\"reason.0\":\"verifier-disagreement\",\"diagnosticCount\":\"0\"}";
        var rows = new[]
        {
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "OutcomeResolved", "Succeeded", payload: payload),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Escalation"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EvaluationJourneyProjector.TryProject(rows));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.DoesNotContain(reason, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Classified", "Maybe")]
    [InlineData("Unknown", null)]
    [InlineData("Unavailable", "Escalation")]
    [InlineData("Classified", null)]
    public void Rejects_verifier_values_outside_the_closed_contract(
        string verifierStatus,
        string? verifierClassification)
    {
        var classificationProperty = verifierClassification is null
            ? string.Empty
            : $",\"verifierClassification\":\"{verifierClassification}\"";
        var payload =
            "{\"mode\":\"enforcement\",\"iteration\":\"1\",\"proposedIntent\":\"Report.Done\",\"workState\":\"Completed\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-v1\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"true\",\"verifierStatus\":\"" +
            verifierStatus +
            "\"" +
            classificationProperty +
            ",\"semanticCompletionCandidate\":\"true\",\"reasonCount\":\"1\",\"reason.0\":\"verifier-disagreement\",\"diagnosticCount\":\"0\"}";
        var rows = new[]
        {
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "OutcomeResolved", "Succeeded", payload: payload),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Escalation"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EvaluationJourneyProjector.TryProject(rows));

        Assert.Equal(
            "Outcome verifier audit value is outside the closed contract.",
            exception.Message);
        Assert.DoesNotContain("Maybe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_outcome_diagnostics_outside_the_closed_contract()
    {
        var rows = new[]
        {
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(
                2,
                "OutcomeResolved",
                "Failed",
                payload: "{\"mode\":\"shadow\",\"iteration\":\"1\",\"proposedIntent\":\"Report.Done\",\"workState\":\"Completed\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-unavailable\",\"policyFingerprint\":\"unavailable\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"false\",\"reasonCount\":\"1\",\"reason.0\":\"policy-unavailable\",\"diagnosticCount\":\"1\",\"diagnostic.0\":\"provider-said-secret\"}"),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Report"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EvaluationJourneyProjector.TryProject(rows));

        Assert.Equal(
            "Outcome resolution diagnostic is outside the closed contract.",
            exception.Message);
        Assert.DoesNotContain("provider-said-secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reason.0", "provider-said-secret")]
    [InlineData("proposedIntent", "Report.Custom")]
    [InlineData("workState", "MostlyDone")]
    [InlineData("requiredIntervention", "AskSomeone")]
    [InlineData("resolvedOutcome", "Maybe")]
    public void Rejects_outcome_values_outside_the_closed_contract(string property, string value)
    {
        var original = property switch
        {
            "reason.0" => "policy-unavailable",
            "proposedIntent" => "Report.Done",
            "workState" => "Completed",
            "requiredIntervention" => "None",
            _ => "Escalation",
        };
        var payload = "{\"mode\":\"shadow\",\"iteration\":\"1\",\"proposedIntent\":\"Report.Done\",\"workState\":\"Completed\",\"requiredIntervention\":\"None\",\"resolvedOutcome\":\"Escalation\",\"policyVersion\":\"outcome-policy-v1\",\"policyFingerprint\":\"sha256:policy\",\"proposalOverridden\":\"true\",\"verifierInvoked\":\"false\",\"reasonCount\":\"1\",\"reason.0\":\"policy-unavailable\",\"diagnosticCount\":\"0\"}"
            .Replace(
                $"\"{property}\":\"{original}\"",
                $"\"{property}\":\"{value}\"",
                StringComparison.Ordinal);
        var rows = new[]
        {
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "OutcomeResolved", "Succeeded", payload: payload),
            Row(3, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"result-emitted\"}"),
            Row(4, "ResultMessageCreated", "Succeeded", messageType: "Report"),
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EvaluationJourneyProjector.TryProject(rows));

        Assert.Equal("Outcome resolution value is outside the closed contract.", exception.Message);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    private static EvaluationAuditRow Row(
        int seconds,
        string stage,
        string outcome,
        string? reasonCode = null,
        string? messageType = null,
        string? providerId = null,
        string? modelId = null,
        int? latencyMilliseconds = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        bool? tokensEstimated = null,
        decimal? costAmount = null,
        string? costCurrency = null,
        bool? costEstimated = null,
        string payload = "{}") =>
        new(
            StartedAt.AddSeconds(seconds),
            stage,
            outcome,
            reasonCode,
            messageType,
            providerId,
            modelId,
            latencyMilliseconds,
            inputTokens,
            outputTokens,
            totalTokens,
            tokensEstimated,
            costAmount,
            costCurrency,
            costEstimated,
            payload);
}
