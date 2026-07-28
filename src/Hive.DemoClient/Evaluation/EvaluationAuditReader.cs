using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Hive.DemoClient.Evaluation;

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
    int? LatencyMilliseconds,
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
            PayloadInt(row.Payload, "maxOutputTokens"));

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
    public static IReadOnlySet<int> SupportedVersions { get; } = new HashSet<int> { 1, 2 };

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
        "outcome_proposal.proposal.next_action",
        "outcome_proposal.proposal.proposed_intent",
        "outcome_proposal.proposal.required_intervention",
        "outcome_proposal.proposal.work_state",
    };
}

public sealed class PostgreSqlEvaluationAuditReader : IEvaluationAuditReader
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlEvaluationAuditReader(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<EvaluationJourney?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT occurred_at_utc, stage, outcome, reason_code, message_type,
                   provider_id, model_id, latency_ms, input_tokens, output_tokens,
                   total_tokens, tokens_estimated, cost_amount, cost_currency,
                   cost_estimated, payload
            FROM audit.journey_events
            WHERE organization_id = @organization_id
              AND thread_id = @thread_id
              AND directive_id = @directive_id
            ORDER BY sequence_id;
            """);
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId;
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = threadId;
        command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value = directiveId;

        var rows = new List<EvaluationAuditRow>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EvaluationAuditRow(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetString(1),
                reader.GetString(2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableInt(reader, 7),
                NullableInt(reader, 8),
                NullableInt(reader, 9),
                NullableInt(reader, 10),
                NullableBool(reader, 11),
                NullableDecimal(reader, 12),
                NullableString(reader, 13),
                NullableBool(reader, 14),
                reader.GetString(15)));
        }

        return EvaluationJourneyProjector.TryProject(rows);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? NullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? NullableBool(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static decimal? NullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

}
