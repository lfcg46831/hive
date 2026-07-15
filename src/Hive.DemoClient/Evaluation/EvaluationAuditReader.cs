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
    EvaluationInvalidOutputDiagnostics? InvalidOutputDiagnostics = null);

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
        EvaluationAuditRow? cost = null;
        EvaluationAuditRow? decision = null;

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
            if (row.Stage == "GatewayCostRecorded") cost = row;
        }

        var failedDecision = decision is not null && IsFailedOrRejected(decision.Outcome)
            ? decision
            : null;
        if (cost is null ||
            (result is null && failedDecision is null) ||
            submissionStartedAt is null ||
            last is null)
        {
            return null;
        }

        var terminal = result is null ? failedDecision! : decision;
        return new EvaluationJourney(
            (terminal?.Outcome ?? result!.Outcome).ToLowerInvariant(),
            result is null
                ? cost.ReasonCode
                    ?? PayloadValue(cost.Payload, "errorCode")
                    ?? PayloadValue(terminal?.Payload, "terminalCode")
                    ?? terminal?.ReasonCode
                : PayloadValue(terminal?.Payload, "terminalCode")
                    ?? result.ReasonCode
                    ?? terminal?.ReasonCode,
            Decision(result?.MessageType),
            cost.ProviderId,
            cost.ModelId,
            PayloadValue(cost.Payload, "outputConstraintMode"),
            cost.InputTokens,
            cost.OutputTokens,
            cost.TotalTokens,
            cost.TokensEstimated,
            cost.CostAmount,
            cost.CostCurrency,
            cost.CostEstimated,
            cost.LatencyMilliseconds,
            Convert.ToInt64(
                (last.Value - submissionStartedAt.Value).TotalMilliseconds,
                CultureInfo.InvariantCulture),
            PayloadValue(cost.Payload, "costStatus") ?? CostStatusFrom(cost),
            PayloadValue(cost.Payload, "pricingVersion"),
            PayloadInt(cost.Payload, "pricingTokenUnit"),
            PayloadDecimal(cost.Payload, "inputPricePerTokenUnit"),
            PayloadDecimal(cost.Payload, "outputPricePerTokenUnit"),
            ParseInvalidOutputDiagnostics(decision?.Payload));
    }

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
        if (version != EvaluationInvalidOutputDiagnosticContract.Version)
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
}

internal static class EvaluationInvalidOutputDiagnosticContract
{
    public const int Version = 1;

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
