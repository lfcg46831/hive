using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts.Audit;

namespace Hive.Evaluation.Tooling.Evaluation;

public interface IEvaluationAuditReader : IAsyncDisposable
{
    Task<EvaluationJourney?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken);
}

public sealed record EvaluationJourney(
    string Outcome,
    string? TerminalCode,
    string? Decision,
    string? ProviderId,
    string? ModelId,
    string? OutputConstraintMode,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    bool? TokensEstimated,
    decimal? CostAmount,
    string? CostCurrency,
    bool? CostEstimated,
    long? GatewayLatencyMilliseconds,
    long JourneyDurationMilliseconds,
    string? CostStatus = null,
    string? PricingVersion = null,
    int? PricingTokenUnit = null,
    decimal? InputPricePerTokenUnit = null,
    decimal? OutputPricePerTokenUnit = null,
    EvaluationInvalidOutputDiagnostics? InvalidOutputDiagnostics = null,
    EvaluationOutcomeResolution? OutcomeResolution = null,
    IReadOnlyList<EvaluationGatewayCall>? GatewayCalls = null,
    IReadOnlyList<EvaluationOutcomeResolution>? OutcomeResolutionSteps = null);

internal sealed record EvaluationAuditRow(
    DateTimeOffset OccurredAt,
    string Stage,
    string Outcome,
    string? ReasonCode,
    string? MessageType,
    string? ProviderId,
    string? ModelId,
    long? LatencyMilliseconds,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    bool? TokensEstimated,
    decimal? CostAmount,
    string? CostCurrency,
    bool? CostEstimated,
    string Payload);

internal static class EvaluationJourneyProjector
{
    public static bool HasTerminalProposalOverride(IEnumerable<EvaluationAuditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var terminalResolution = rows
            .LastOrDefault(row => row.Stage == "OutcomeResolved");
        return terminalResolution is not null &&
            string.Equals(
                PayloadValue(terminalResolution.Payload, "proposalOverridden"),
                "true",
                StringComparison.Ordinal);
    }

    public static EvaluationJourney? TryProject(IEnumerable<EvaluationAuditRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        DateTimeOffset? submissionStartedAt = null;
        DateTimeOffset? last = null;
        EvaluationAuditRow? result = null;
        var costs = new List<EvaluationAuditRow>();
        EvaluationAuditRow? decision = null;
        var outcomeResolutions = new List<EvaluationAuditRow>();

        foreach (var row in rows)
        {
            if (row.Stage == "SubmissionReceived")
            {
                submissionStartedAt = submissionStartedAt is null || row.OccurredAt < submissionStartedAt
                    ? row.OccurredAt
                    : submissionStartedAt;
            }
            last = last is null || row.OccurredAt > last ? row.OccurredAt : last;
            if (row.Stage == "AgentDecided") decision = row;
            if (row.Stage == "ResultMessageCreated") result = row;
            if (row.Stage == "GatewayCostRecorded") costs.Add(row);
            if (row.Stage == "OutcomeResolved") outcomeResolutions.Add(row);
        }

        var failedDecision = decision is not null && IsFailedOrRejected(decision.Outcome)
            ? decision
            : null;
        if (costs.Count == 0 ||
            (result is null && failedDecision is null) ||
            submissionStartedAt is null ||
            last is null)
        {
            return null;
        }

        var terminal = result is null ? failedDecision! : decision;
        var terminalCost = costs[^1];
        var gatewayCalls = costs
            .Select((row, index) => ProjectGatewayCall(row, index + 1))
            .ToArray();
        var aggregate = AggregateGatewayCalls(gatewayCalls);
        var outcomeResolutionSteps = outcomeResolutions
            .Select(ParseOutcomeResolution)
            .OfType<EvaluationOutcomeResolution>()
            .ToArray();
        return new EvaluationJourney(
            (terminal?.Outcome ?? result!.Outcome).ToLowerInvariant(),
            result is null
                ? terminalCost.ReasonCode
                    ?? PayloadValue(terminalCost.Payload, "errorCode")
                    ?? PayloadValue(terminal?.Payload, "terminalCode")
                    ?? terminal?.ReasonCode
                : PayloadValue(terminal?.Payload, "terminalCode")
                    ?? result.ReasonCode
                    ?? terminal?.ReasonCode,
            Decision(result?.MessageType),
            aggregate.ProviderId,
            aggregate.ModelId,
            aggregate.OutputConstraintMode,
            aggregate.InputTokens,
            aggregate.OutputTokens,
            aggregate.TotalTokens,
            aggregate.TokensEstimated,
            aggregate.CostAmount,
            aggregate.CostCurrency,
            aggregate.CostEstimated,
            aggregate.LatencyMilliseconds,
            Convert.ToInt64(
                (last.Value - submissionStartedAt.Value).TotalMilliseconds,
                CultureInfo.InvariantCulture),
            aggregate.CostStatus,
            aggregate.PricingVersion,
            aggregate.PricingTokenUnit,
            aggregate.InputPricePerTokenUnit,
            aggregate.OutputPricePerTokenUnit,
            ParseInvalidOutputDiagnostics(decision?.Payload),
            outcomeResolutionSteps.LastOrDefault(),
            gatewayCalls,
            outcomeResolutionSteps);
    }

    private static EvaluationGatewayCall ProjectGatewayCall(
        EvaluationAuditRow row,
        int callIndex) =>
        new(
            callIndex,
            PayloadValue(row.Payload, "operation") ?? "unspecified",
            PayloadInt(row.Payload, "iteration") ?? callIndex,
            row.Outcome.ToLowerInvariant(),
            row.ReasonCode ?? PayloadValue(row.Payload, "errorCode"),
            row.ProviderId,
            row.ModelId,
            PayloadValue(row.Payload, "outputConstraintMode"),
            row.InputTokens,
            row.OutputTokens,
            row.TotalTokens,
            row.TokensEstimated,
            row.CostAmount,
            row.CostCurrency,
            row.CostEstimated,
            row.LatencyMilliseconds,
            PayloadValue(row.Payload, "costStatus") ?? CostStatusFrom(row),
            PayloadValue(row.Payload, "pricingVersion"),
            PayloadInt(row.Payload, "pricingTokenUnit"),
            PayloadDecimal(row.Payload, "inputPricePerTokenUnit"),
            PayloadDecimal(row.Payload, "outputPricePerTokenUnit"),
            PayloadValue(row.Payload, "finishReason"),
            PayloadInt(row.Payload, "providerStatusCode"),
            PayloadDouble(row.Payload, "requestTimeoutMilliseconds"),
            PayloadInt(row.Payload, "maxOutputTokens"),
            PayloadInt(row.Payload, "executionLimitsVersion"),
            PayloadDouble(row.Payload, "executionBudgetMilliseconds"),
            PayloadDouble(row.Payload, "perCallTimeoutMilliseconds"));

    private static EvaluationGatewayAggregate AggregateGatewayCalls(
        IReadOnlyList<EvaluationGatewayCall> calls)
    {
        var usageComplete = calls.All(call =>
            call.InputTokens.HasValue &&
            call.OutputTokens.HasValue &&
            call.TotalTokens.HasValue &&
            call.TokensEstimated.HasValue);
        var costComplete = calls.All(call =>
            call.CostAmount.HasValue &&
            call.CostCurrency is not null &&
            call.CostEstimated.HasValue) &&
            CommonValue(calls.Select(call => call.CostCurrency)) is not null;
        var latencyComplete = calls.All(call => call.LatencyMilliseconds.HasValue);

        return new EvaluationGatewayAggregate(
            CommonValue(calls.Select(call => call.ProviderId)),
            CommonValue(calls.Select(call => call.ModelId)),
            CommonValue(calls.Select(call => call.OutputConstraintMode)),
            usageComplete ? calls.Sum(call => call.InputTokens!.Value) : null,
            usageComplete ? calls.Sum(call => call.OutputTokens!.Value) : null,
            usageComplete ? calls.Sum(call => call.TotalTokens!.Value) : null,
            usageComplete ? calls.Any(call => call.TokensEstimated == true) : null,
            costComplete ? calls.Sum(call => call.CostAmount!.Value) : null,
            costComplete ? CommonValue(calls.Select(call => call.CostCurrency)) : null,
            costComplete ? calls.Any(call => call.CostEstimated == true) : null,
            latencyComplete ? calls.Sum(call => (long)call.LatencyMilliseconds!.Value) : null,
            costComplete
                ? calls.Any(call => call.CostStatus == "estimated")
                    ? "estimated"
                    : "provider-reported"
                : "cost-unavailable",
            costComplete ? CommonValue(calls.Select(call => call.PricingVersion)) : null,
            costComplete ? CommonValue(calls.Select(call => call.PricingTokenUnit)) : null,
            costComplete
                ? CommonValue(calls.Select(call => call.InputPricePerTokenUnit))
                : null,
            costComplete
                ? CommonValue(calls.Select(call => call.OutputPricePerTokenUnit))
                : null);
    }

    private static T? CommonValue<T>(IEnumerable<T?> values)
    {
        var distinct = values
            .Where(value => value is not null)
            .Distinct()
            .Take(2)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : default;
    }

    private sealed record EvaluationGatewayAggregate(
        string? ProviderId,
        string? ModelId,
        string? OutputConstraintMode,
        int? InputTokens,
        int? OutputTokens,
        int? TotalTokens,
        bool? TokensEstimated,
        decimal? CostAmount,
        string? CostCurrency,
        bool? CostEstimated,
        long? LatencyMilliseconds,
        string CostStatus,
        string? PricingVersion,
        int? PricingTokenUnit,
        decimal? InputPricePerTokenUnit,
        decimal? OutputPricePerTokenUnit);

    private static bool IsFailedOrRejected(string outcome) =>
        outcome is "Failed" or "Rejected";

    private static string? Decision(string? messageType) => messageType switch
    {
        "Report" => "report",
        "Escalation" => "escalation",
        _ => null,
    };

    private static string? PayloadValue(string? payload, string property)
    {
        if (payload is null) return null;
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(property, out var value)
            ? value.GetString()
            : null;
    }

    private static string CostStatusFrom(EvaluationAuditRow cost) =>
        cost.CostAmount is null
            ? "cost-unavailable"
            : cost.CostEstimated == true
                ? "estimated"
                : "provider-reported";

    private static int? PayloadInt(string? payload, string property)
    {
        var value = PayloadValue(payload, property);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : throw new InvalidOperationException(
                $"Evaluation audit payload '{property}' is invalid.");
    }

    private static decimal? PayloadDecimal(string? payload, string property)
    {
        var value = PayloadValue(payload, property);
        if (value is null)
        {
            return null;
        }

        return decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : throw new InvalidOperationException(
                $"Evaluation audit payload '{property}' is invalid.");
    }

    private static double? PayloadDouble(string? payload, string property)
    {
        var value = PayloadValue(payload, property);
        if (value is null)
        {
            return null;
        }

        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : throw new InvalidOperationException(
                $"Evaluation audit payload '{property}' is invalid.");
    }

    private static EvaluationInvalidOutputDiagnostics? ParseInvalidOutputDiagnostics(
        string? payload)
    {
        var count = PayloadInt(payload, "parseErrorCount");
        if (count is null or 0)
        {
            return null;
        }

        if (count < 0)
        {
            throw new InvalidOperationException("Evaluation parse diagnostic count is invalid.");
        }

        var version = PayloadInt(payload, "parseErrorContractVersion");
        if (version is null ||
            !EvaluationInvalidOutputDiagnosticContract.SupportedVersions.Contains(version.Value))
        {
            throw new InvalidOperationException("Evaluation parse diagnostic contract version is unsupported.");
        }

        var errors = new List<EvaluationInvalidOutputDiagnostic>(count.Value);
        for (var index = 0; index < count.Value; index++)
        {
            var path = PayloadValue(payload, $"parseError.{index}.path");
            var code = PayloadValue(payload, $"parseError.{index}.code");
            if (path is null || code is null ||
                !EvaluationInvalidOutputDiagnosticContract.Paths.Contains(path) ||
                !EvaluationInvalidOutputDiagnosticContract.Codes.Contains(code))
            {
                throw new InvalidOperationException(
                    "Evaluation parse diagnostic is outside the closed contract.");
            }

            errors.Add(new EvaluationInvalidOutputDiagnostic(path, code));
        }

        var ordered = errors
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        if (!errors.SequenceEqual(ordered))
        {
            throw new InvalidOperationException(
                "Evaluation parse diagnostics are not canonically ordered.");
        }

        return new EvaluationInvalidOutputDiagnostics(version.Value, count.Value, ordered);
    }

    private static EvaluationOutcomeResolution? ParseOutcomeResolution(EvaluationAuditRow? row)
    {
        if (row is null)
        {
            return null;
        }

        var mode = RequiredPayloadValue(row.Payload, "mode");
        if (mode is not ("shadow" or "enforcement"))
        {
            throw new InvalidOperationException("Outcome resolution mode is outside the closed contract.");
        }

        var reasonCount = RequiredNonNegativePayloadInt(row.Payload, "reasonCount");
        var diagnosticCount = RequiredNonNegativePayloadInt(row.Payload, "diagnosticCount");
        var reasons = Enumerable.Range(0, reasonCount)
            .Select(index => RequiredPayloadValue(row.Payload, $"reason.{index}"))
            .ToArray();
        var diagnostics = Enumerable.Range(0, diagnosticCount)
            .Select(index => RequiredPayloadValue(row.Payload, $"diagnostic.{index}"))
            .ToArray();
        if (diagnostics.Any(code => !OutcomeResolutionDiagnosticCodes.Contains(code)))
        {
            throw new InvalidOperationException(
                "Outcome resolution diagnostic is outside the closed contract.");
        }

        if (reasons.Any(code => !OutcomeResolutionReasonCodes.Contains(code)) ||
            !OutcomeProposedIntentCodes.Contains(RequiredPayloadValue(row.Payload, "proposedIntent")) ||
            !OutcomeWorkStateCodes.Contains(RequiredPayloadValue(row.Payload, "workState")) ||
            !OutcomeRequiredInterventionCodes.Contains(
                RequiredPayloadValue(row.Payload, "requiredIntervention")) ||
            !OutcomeKindCodes.Contains(RequiredPayloadValue(row.Payload, "resolvedOutcome")))
        {
            throw new InvalidOperationException(
                "Outcome resolution value is outside the closed contract.");
        }

        var verifierStatus = PayloadValue(row.Payload, "verifierStatus");
        var verifierClassification = PayloadValue(row.Payload, "verifierClassification");
        if ((verifierStatus is not null && !OutcomeVerifierStatusCodes.Contains(verifierStatus)) ||
            (verifierClassification is not null &&
             !OutcomeVerifierClassificationCodes.Contains(verifierClassification)) ||
            (verifierStatus == "Classified") != (verifierClassification is not null))
        {
            throw new InvalidOperationException(
                "Outcome verifier audit value is outside the closed contract.");
        }

        var ineligibilityReasons = ParseSemanticCompletionIneligibilityReasons(row.Payload);
        var semanticCompletionCandidate =
            OptionalPayloadBool(row.Payload, "semanticCompletionCandidate");
        if (ineligibilityReasons is not null &&
            (semanticCompletionCandidate == true) != (ineligibilityReasons.Count == 0))
        {
            throw new InvalidOperationException(
                "Semantic-completion eligibility audit values are inconsistent.");
        }

        var deadlineRemainingMilliseconds =
            OptionalNonNegativePayloadLong(row.Payload, "deadlineRemainingMilliseconds");
        return new EvaluationOutcomeResolution(
            mode,
            RequiredNonNegativePayloadInt(row.Payload, "iteration"),
            RequiredPayloadValue(row.Payload, "proposedIntent"),
            RequiredPayloadValue(row.Payload, "workState"),
            RequiredPayloadValue(row.Payload, "requiredIntervention"),
            RequiredPayloadValue(row.Payload, "resolvedOutcome"),
            reasons,
            RequiredPayloadValue(row.Payload, "policyVersion"),
            RequiredPayloadValue(row.Payload, "policyFingerprint"),
            RequiredPayloadBool(row.Payload, "proposalOverridden"),
            RequiredPayloadBool(row.Payload, "verifierInvoked"),
            diagnostics,
            row.ProviderId,
            row.ModelId,
            row.InputTokens,
            row.OutputTokens,
            row.TotalTokens,
            row.CostAmount,
            row.CostCurrency,
            row.LatencyMilliseconds,
            verifierStatus,
            verifierClassification,
            semanticCompletionCandidate,
            ineligibilityReasons,
            deadlineRemainingMilliseconds);
    }

    private static IReadOnlyList<string>? ParseSemanticCompletionIneligibilityReasons(
        string payload)
    {
        var count = OptionalNonNegativePayloadInt(
            payload,
            "semanticCompletionIneligibilityReasonCount");
        if (count is null)
        {
            return null;
        }

        var reasons = Enumerable.Range(0, count.Value)
            .Select(index => RequiredPayloadValue(
                payload,
                $"semanticCompletionIneligibilityReason.{index}"))
            .ToArray();
        if (reasons.Distinct(StringComparer.Ordinal).Count() != reasons.Length ||
            reasons.Any(reason =>
                !OutcomeSemanticCompletionIneligibilityReasonCodes.Contains(reason)))
        {
            throw new InvalidOperationException(
                "Semantic-completion ineligibility reason is outside the closed contract.");
        }

        var ordered = reasons
            .OrderBy(reason =>
                OutcomeSemanticCompletionIneligibilityReasonOrder[reason])
            .ToArray();
        if (!reasons.SequenceEqual(ordered, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Semantic-completion ineligibility reasons are not canonically ordered.");
        }

        return reasons;
    }

    private static string RequiredPayloadValue(string payload, string property) =>
        PayloadValue(payload, property)
        ?? throw new InvalidOperationException(
            $"Outcome resolution audit payload '{property}' is missing.");

    private static int RequiredNonNegativePayloadInt(string payload, string property)
    {
        var value = PayloadInt(payload, property);
        return value is >= 0
            ? value.Value
            : throw new InvalidOperationException(
                $"Outcome resolution audit payload '{property}' is invalid.");
    }

    private static int? OptionalNonNegativePayloadInt(string payload, string property)
    {
        var value = PayloadInt(payload, property);
        return value is null or >= 0
            ? value
            : throw new InvalidOperationException(
                $"Outcome resolution audit payload '{property}' is invalid.");
    }

    private static long? OptionalNonNegativePayloadLong(string payload, string property)
    {
        var value = PayloadValue(payload, property);
        if (value is null)
        {
            return null;
        }

        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var result) &&
            result >= 0
                ? result
                : throw new InvalidOperationException(
                    $"Outcome resolution audit payload '{property}' is invalid.");
    }

    private static bool RequiredPayloadBool(string payload, string property)
    {
        var value = RequiredPayloadValue(payload, property);
        return value switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException(
                $"Outcome resolution audit payload '{property}' is invalid."),
        };
    }

    private static bool? OptionalPayloadBool(string payload, string property)
    {
        var value = PayloadValue(payload, property);
        return value switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException(
                $"Outcome resolution audit payload '{property}' is invalid."),
        };
    }

    private static readonly IReadOnlySet<string> OutcomeResolutionDiagnosticCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "facts-unavailable",
            "policy-unavailable",
            "policy-incompatible",
            "resolution-unavailable",
            "materialization-incompatible",
        };

    private static readonly IReadOnlySet<string> OutcomeProposedIntentCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ContinueWork",
            "Report.Progress",
            "Report.Done",
            "Escalation",
            "Directive",
            "ApprovalRequired",
        };

    private static readonly IReadOnlySet<string> OutcomeWorkStateCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NotStarted",
            "InProgress",
            "Blocked",
            "Completed",
            "Failed",
        };

    private static readonly IReadOnlySet<string> OutcomeRequiredInterventionCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "None",
            "HumanApproval",
            "SuperiorDecision",
            "ExternalAction",
            "Delegation",
        };

    private static readonly IReadOnlySet<string> OutcomeKindCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ContinueWork",
            "Report.Progress",
            "Report.Done",
            "Escalation",
            "Directive",
            "ApprovalRequired",
            "Undetermined",
        };

    private static readonly IReadOnlySet<string> OutcomeVerifierStatusCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Classified",
            "Unavailable",
            "TimedOut",
            "InvalidOutput",
        };

    private static readonly IReadOnlySet<string> OutcomeVerifierClassificationCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ContinueWork",
            "Report.Progress",
            "Report.Done",
            "Escalation",
            "Directive",
            "ApprovalRequired",
            "Undetermined",
        };

    private static readonly IReadOnlyDictionary<string, int>
        OutcomeSemanticCompletionIneligibilityReasonOrder =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["proposal-intent-not-report-done"] = 1,
                ["work-state-not-completed"] = 2,
                ["intervention-required"] = 3,
                ["blockers-present"] = 4,
                ["next-action-present"] = 5,
                ["structured-completion-criteria-present"] = 6,
                ["completion-state-incompatible"] = 7,
                ["evidence-references-missing"] = 8,
                ["evidence-source-not-directive-input"] = 9,
                ["evidence-reference-not-in-context"] = 10,
            };

    private static readonly IReadOnlySet<string>
        OutcomeSemanticCompletionIneligibilityReasonCodes =
            OutcomeSemanticCompletionIneligibilityReasonOrder.Keys
                .ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> OutcomeResolutionReasonCodes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "human-approval-gate",
            "approval-pending",
            "deadline-exceeded",
            "budget-exhausted",
            "iteration-limit-reached",
            "retry-limit-reached",
            "permanent-dependency-failure",
            "authority-denied",
            "routing-unavailable",
            "policy-trigger-observed",
            "autonomous-action-available",
            "delegation-required",
            "completion-criteria-satisfied",
            "verifiable-progress",
            "insufficient-facts",
            "contradictory-facts",
            "verifier-confirmed",
            "verifier-unavailable",
            "verifier-timed-out",
            "verifier-output-invalid",
            "verifier-contradicted-facts",
            "verifier-disagreement",
            "facts-unavailable",
            "policy-unavailable",
            "policy-incompatible",
            "proposal-escalation",
            "semantic-completion-verified",
        };
}

internal static class EvaluationInvalidOutputDiagnosticContract
{
    public static IReadOnlySet<int> SupportedVersions { get; } = new HashSet<int> { 1, 2, 3 };

    public static IReadOnlySet<string> Codes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "empty-response",
        "invalid-field",
        "invalid-intent",
        "invalid-json",
        "invalid-schema-version",
        "payload-ambiguous",
        "payload-intent-mismatch",
        "payload-required",
        "required-field",
        "top-level-object-required",
        "unknown-field",
        "invalid-vocabulary",
        "duplicate-field",
        "contradictory-combination",
    };

    public static IReadOnlySet<string> Paths { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "$",
        "acting_under",
        "decision",
        "decision.directive",
        "decision.directive.context",
        "decision.directive.objective",
        "decision.directive.target_position_id",
        "decision.escalation",
        "decision.escalation.context",
        "decision.escalation.issue",
        "decision.escalation.options_considered",
        "decision.escalation.options_considered.item",
        "decision.intent",
        "decision.report",
        "decision.report.body",
        "decision.report.kind",
        "directive",
        "directive.context",
        "directive.objective",
        "directive.target_position_id",
        "escalation",
        "escalation.context",
        "escalation.issue",
        "escalation.options_considered",
        "escalation.options_considered.item",
        "intent",
        "report",
        "report.body",
        "report.kind",
        "schema_version",
        "outcome_proposal",
        "outcome_proposal.schema_version",
        "outcome_proposal.proposal",
        "outcome_proposal.proposal.blockers",
        "outcome_proposal.proposal.blockers.item",
        "outcome_proposal.proposal.evidence_references",
        "outcome_proposal.proposal.evidence_references.item",
        "outcome_proposal.proposal.evidence_references.item.reference",
        "outcome_proposal.proposal.evidence_references.item.source",
        "outcome_proposal.proposal.information_gaps",
        "outcome_proposal.proposal.information_gaps.item",
        "outcome_proposal.proposal.information_gaps.item.materiality",
        "outcome_proposal.proposal.information_gaps.item.materiality_reason",
        "outcome_proposal.proposal.information_gaps.item.missing_evidence_reference",
        "outcome_proposal.proposal.next_action",
        "outcome_proposal.proposal.proposed_intent",
        "outcome_proposal.proposal.authority_request",
        "outcome_proposal.proposal.authority_request.authority_kind",
        "outcome_proposal.proposal.authority_request.authority_reference",
        "outcome_proposal.proposal.authority_request.decision",
        "outcome_proposal.proposal.authority_request.position_limit_reason",
        "outcome_proposal.proposal.required_intervention",
        "outcome_proposal.proposal.work_state",
    };
}

public sealed class HttpEvaluationAuditReader :
    IEvaluationAuditReader,
    IEvaluationProjectionReader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUrl;
    private readonly EvaluationRubric _rubric;
    private readonly ConcurrentDictionary<
        (string OrganizationId, Guid ThreadId, Guid DirectiveId),
        EvaluationAuditExportSnapshot> _terminal = new();

    public HttpEvaluationAuditReader(
        HttpClient httpClient,
        Uri baseUrl,
        EvaluationRubric rubric)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _rubric = rubric ?? throw new ArgumentNullException(nameof(rubric));
    }

    public async Task<EvaluationJourney?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadExportAsync(
                organizationId,
                threadId,
                directiveId,
                cancellationToken)
            .ConfigureAwait(false);
        return snapshot.IsTerminal
            ? EvaluationJourneyProjector.TryProject(snapshot.Rows)
            : null;
    }

    async Task<EvaluationPrediction?> IEvaluationProjectionReader.ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadExportAsync(
                organizationId,
                threadId,
                directiveId,
                cancellationToken)
            .ConfigureAwait(false);
        return snapshot.Result is { } result
            ? _rubric.ProjectResult(
                result.MessageType,
                result.Content,
                EvaluationJourneyProjector.HasTerminalProposalOverride(snapshot.Rows)
                    ? result.AcceptedObservation?.Content
                    : null)
            : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<EvaluationAuditExportSnapshot> ReadExportAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        var key = (organizationId, threadId, directiveId);
        if (_terminal.TryGetValue(key, out var terminal))
        {
            return terminal;
        }

        var cursor = 0L;
        var rows = new List<EvaluationAuditRow>();
        AuditExportResult? result = null;
        var isTerminal = false;
        while (true)
        {
            var path =
                $"/api/v1/organizations/{Uri.EscapeDataString(organizationId)}" +
                $"/threads/{threadId:D}/directives/{directiveId:D}" +
                $"/audit-export?after_sequence={cursor.ToString(CultureInfo.InvariantCulture)}";
            using var response = await _httpClient.GetAsync(
                    new Uri(_baseUrl, path),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException(
                    $"Directive audit/export returned HTTP {(int)response.StatusCode}.");
            }

            var page = await response.Content
                .ReadFromJsonAsync<DirectiveAuditExportPage>(
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "Directive audit/export returned an empty response.");
            if (!string.Equals(
                    page.ContractName,
                    AuditExportContract.Name,
                    StringComparison.Ordinal) ||
                page.ContractVersion != AuditExportContract.Version ||
                page.OrganizationId != organizationId ||
                page.ThreadId != threadId ||
                page.DirectiveId != directiveId ||
                page.AfterSequence != cursor)
            {
                throw new InvalidDataException(
                    "Directive audit/export returned an incompatible scope or contract.");
            }

            rows.AddRange(page.Events.Select(ToRow));
            isTerminal = page.IsTerminal;
            result = page.Result ?? result;
            cursor = page.NextAfterSequence;
            if (page.Events.Count < AuditExportContractLimits.MaxEventsPerPage)
            {
                break;
            }
        }

        var snapshot = new EvaluationAuditExportSnapshot(
            isTerminal,
            rows,
            result);
        if (isTerminal)
        {
            _terminal.TryAdd(key, snapshot);
        }

        return snapshot;
    }

    private static EvaluationAuditRow ToRow(AuditExportEvent item) =>
        new(
            item.OccurredAtUtc,
            item.Stage,
            item.Outcome,
            item.ReasonCode,
            item.MessageType,
            item.Provider?.ProviderId,
            item.Provider?.ModelId,
            item.LatencyMilliseconds,
            item.Usage?.InputTokens,
            item.Usage?.OutputTokens,
            item.Usage?.TotalTokens,
            item.Usage?.Estimated,
            item.Cost?.Amount,
            item.Cost?.Currency,
            item.Cost?.Estimated,
            JsonSerializer.Serialize(item.Attributes, JsonOptions));

    private sealed record EvaluationAuditExportSnapshot(
        bool IsTerminal,
        IReadOnlyList<EvaluationAuditRow> Rows,
        AuditExportResult? Result);
}
