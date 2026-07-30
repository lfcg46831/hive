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

    public static JourneyAuditRecord Read(
        NpgsqlDataReader reader,
        int ordinalOffset = 0)
    {
        var providerId = ReadNullableString(reader, ordinalOffset + 11);
        var modelId = ReadNullableString(reader, ordinalOffset + 12);
        var inputTokens = ReadNullableInt(reader, ordinalOffset + 15);
        var outputTokens = ReadNullableInt(reader, ordinalOffset + 16);
        var totalTokens = ReadNullableInt(reader, ordinalOffset + 17);
        var tokensEstimated = ReadNullableBool(reader, ordinalOffset + 18);
        var costAmount = ReadNullableDecimal(reader, ordinalOffset + 19);
        var costCurrency = ReadNullableString(reader, ordinalOffset + 20);
        var costEstimated = ReadNullableBool(reader, ordinalOffset + 21);

        return new JourneyAuditRecord(
            reader.GetGuid(ordinalOffset),
            reader.GetFieldValue<DateTimeOffset>(ordinalOffset + 1),
            Enum.Parse<JourneyAuditStage>(reader.GetString(ordinalOffset + 3)),
            Enum.Parse<JourneyAuditOutcome>(reader.GetString(ordinalOffset + 4)),
            OrganizationId.From(reader.GetString(ordinalOffset + 6)),
            ThreadId.From(reader.GetGuid(ordinalOffset + 7)),
            MessageId.From(reader.GetGuid(ordinalOffset + 9)),
            reader.IsDBNull(ordinalOffset + 8)
                ? null
                : DirectiveId.From(reader.GetGuid(ordinalOffset + 8)),
            reader.IsDBNull(ordinalOffset + 10)
                ? null
                : PositionId.From(reader.GetString(ordinalOffset + 10)),
            ReadNullableString(reader, ordinalOffset + 5),
            ReadNullableString(reader, ordinalOffset + 13),
            providerId is null || modelId is null ? null : new AiProviderMetadata(providerId, modelId),
            inputTokens is null && outputTokens is null && totalTokens is null && tokensEstimated is null
                ? null
                : new AiTokenUsage(inputTokens, outputTokens, totalTokens, tokensEstimated ?? false),
            costAmount is null || costCurrency is null || costEstimated is null
                ? null
                : new AiCostMetadata(costAmount.Value, costCurrency, costEstimated.Value),
            ReadNullableInt(reader, ordinalOffset + 14) is { } latencyMs
                ? TimeSpan.FromMilliseconds(latencyMs)
                : null,
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                reader.GetString(ordinalOffset + 22),
                JsonOptions),
            reader.GetFieldValue<DateTimeOffset>(ordinalOffset + 2));
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
