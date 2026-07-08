using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Npgsql;

namespace Hive.Infrastructure.Auditing.PostgreSql;

internal static class PostgreSqlJourneyAuditRecordReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string SelectColumns = """
        audit_event_id,
        occurred_at_utc,
        persisted_at_utc,
        stage,
        outcome,
        reason_code,
        organization_id,
        thread_id,
        directive_id,
        message_id,
        position_id,
        provider_id,
        model_id,
        message_type,
        latency_ms,
        input_tokens,
        output_tokens,
        total_tokens,
        tokens_estimated,
        cost_amount,
        cost_currency,
        cost_estimated,
        payload
        """;

    public static JourneyAuditRecord Read(NpgsqlDataReader reader)
    {
        var providerId = ReadNullableString(reader, 11);
        var modelId = ReadNullableString(reader, 12);
        var inputTokens = ReadNullableInt(reader, 15);
        var outputTokens = ReadNullableInt(reader, 16);
        var totalTokens = ReadNullableInt(reader, 17);
        var tokensEstimated = ReadNullableBool(reader, 18);
        var costAmount = ReadNullableDecimal(reader, 19);
        var costCurrency = ReadNullableString(reader, 20);
        var costEstimated = ReadNullableBool(reader, 21);

        return new JourneyAuditRecord(
            reader.GetGuid(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            Enum.Parse<JourneyAuditStage>(reader.GetString(3)),
            Enum.Parse<JourneyAuditOutcome>(reader.GetString(4)),
            OrganizationId.From(reader.GetString(6)),
            ThreadId.From(reader.GetGuid(7)),
            MessageId.From(reader.GetGuid(9)),
            reader.IsDBNull(8) ? null : DirectiveId.From(reader.GetGuid(8)),
            reader.IsDBNull(10) ? null : PositionId.From(reader.GetString(10)),
            ReadNullableString(reader, 5),
            ReadNullableString(reader, 13),
            providerId is null || modelId is null ? null : new AiProviderMetadata(providerId, modelId),
            inputTokens is null && outputTokens is null && totalTokens is null && tokensEstimated is null
                ? null
                : new AiTokenUsage(inputTokens, outputTokens, totalTokens, tokensEstimated ?? false),
            costAmount is null || costCurrency is null || costEstimated is null
                ? null
                : new AiCostMetadata(costAmount.Value, costCurrency, costEstimated.Value),
            ReadNullableInt(reader, 14) is { } latencyMs
                ? TimeSpan.FromMilliseconds(latencyMs)
                : null,
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.GetString(22),
                JsonOptions),
            reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNullableInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? ReadNullableBool(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
}
