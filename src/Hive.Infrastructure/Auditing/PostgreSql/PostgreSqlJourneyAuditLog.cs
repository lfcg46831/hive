using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Auditing.PostgreSql;

public sealed class PostgreSqlJourneyAuditLog : IJourneyAuditLog, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlJourneyAuditLog(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be empty or whitespace.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
    }

    public PostgreSqlJourneyAuditLog(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Append(JourneyAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var command = _dataSource.CreateCommand(
            $"""
            INSERT INTO {JourneyAuditSchema.SchemaName}.journey_events (
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
                payload)
            VALUES (
                @audit_event_id,
                @occurred_at_utc,
                @persisted_at_utc,
                @stage,
                @outcome,
                @reason_code,
                @organization_id,
                @thread_id,
                @directive_id,
                @message_id,
                @position_id,
                @provider_id,
                @model_id,
                @message_type,
                @latency_ms,
                @input_tokens,
                @output_tokens,
                @total_tokens,
                @tokens_estimated,
                @cost_amount,
                @cost_currency,
                @cost_estimated,
                @payload)
            ON CONFLICT (audit_event_id) DO NOTHING;
            """);

        AddParameters(command, record);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JourneyAuditRecord> ReadByThread(
        ThreadId threadId,
        DirectiveId? directiveId = null)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        using var command = _dataSource.CreateCommand(
            $"""
            SELECT
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
            FROM {JourneyAuditSchema.SchemaName}.journey_events
            WHERE thread_id = @thread_id
              AND (@directive_id IS NULL OR directive_id = @directive_id)
            ORDER BY sequence_id;
            """);
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = threadId.Value;
        command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value =
            directiveId?.Value ?? (object)DBNull.Value;

        var records = new List<JourneyAuditRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static void AddParameters(NpgsqlCommand command, JourneyAuditRecord record)
    {
        command.Parameters.Add("audit_event_id", NpgsqlDbType.Uuid).Value = record.AuditEventId;
        command.Parameters.Add("occurred_at_utc", NpgsqlDbType.TimestampTz).Value = record.OccurredAtUtc;
        command.Parameters.Add("persisted_at_utc", NpgsqlDbType.TimestampTz).Value = record.PersistedAtUtc;
        command.Parameters.Add("stage", NpgsqlDbType.Text).Value = record.Stage.ToString();
        command.Parameters.Add("outcome", NpgsqlDbType.Text).Value = record.Outcome.ToString();
        command.Parameters.Add("reason_code", NpgsqlDbType.Text).Value =
            record.ReasonCode ?? (object)DBNull.Value;
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = record.OrganizationId.Value;
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = record.ThreadId.Value;
        command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value =
            record.DirectiveId?.Value ?? (object)DBNull.Value;
        command.Parameters.Add("message_id", NpgsqlDbType.Uuid).Value = record.MessageId.Value;
        command.Parameters.Add("position_id", NpgsqlDbType.Text).Value =
            record.PositionId?.Value ?? (object)DBNull.Value;
        command.Parameters.Add("provider_id", NpgsqlDbType.Text).Value =
            record.Provider?.ProviderId ?? (object)DBNull.Value;
        command.Parameters.Add("model_id", NpgsqlDbType.Text).Value =
            record.Provider?.ModelId ?? (object)DBNull.Value;
        command.Parameters.Add("message_type", NpgsqlDbType.Text).Value =
            record.MessageType ?? (object)DBNull.Value;
        command.Parameters.Add("latency_ms", NpgsqlDbType.Integer).Value =
            record.Latency is null
                ? DBNull.Value
                : (object)(int)Math.Round(record.Latency.Value.TotalMilliseconds);
        command.Parameters.Add("input_tokens", NpgsqlDbType.Integer).Value =
            record.Usage?.InputTokens ?? (object)DBNull.Value;
        command.Parameters.Add("output_tokens", NpgsqlDbType.Integer).Value =
            record.Usage?.OutputTokens ?? (object)DBNull.Value;
        command.Parameters.Add("total_tokens", NpgsqlDbType.Integer).Value =
            record.Usage?.TotalTokens ?? (object)DBNull.Value;
        command.Parameters.Add("tokens_estimated", NpgsqlDbType.Boolean).Value =
            record.Usage?.IsEstimated ?? (object)DBNull.Value;
        command.Parameters.Add("cost_amount", NpgsqlDbType.Numeric).Value =
            record.Cost?.Amount ?? (object)DBNull.Value;
        command.Parameters.Add("cost_currency", NpgsqlDbType.Text).Value =
            record.Cost?.Currency ?? (object)DBNull.Value;
        command.Parameters.Add("cost_estimated", NpgsqlDbType.Boolean).Value =
            record.Cost?.IsEstimated ?? (object)DBNull.Value;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
            JsonSerializer.Serialize(record.Payload, JsonOptions);
    }

    private static JourneyAuditRecord ReadRecord(NpgsqlDataReader reader)
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
