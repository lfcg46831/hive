using Hive.Domain.Identity;
using Hive.Domain.Scheduling;
using Npgsql;

namespace Hive.Infrastructure.Scheduling.PostgreSql;

public sealed class PostgreSqlSchedulerPulseDeliveryStore : ISchedulerPulseDeliveryStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlSchedulerPulseDeliveryStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be empty or whitespace.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
    }

    public PostgreSqlSchedulerPulseDeliveryStore(NpgsqlDataSource dataSource)
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

    public async Task<SchedulerPulseDeliveryState> RecordFiredAsync(
        SchedulerPulseDeliveryRecord delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await FindAsync(
            connection,
            transaction,
            delivery.IdempotencyKey,
            cancellationToken)
            .ConfigureAwait(false);

        if (current is null)
        {
            await InsertCurrentAsync(
                connection,
                transaction,
                delivery,
                SchedulerPulseDeliveryStatus.Registered,
                attemptCount: 1,
                reason: null,
                cancellationToken)
                .ConfigureAwait(false);
            await AppendHistoryAsync(
                connection,
                transaction,
                delivery.IdempotencyKey,
                sequence: 1,
                SchedulerPulseDeliveryStatus.Registered,
                delivery.OccurredAtUtc,
                reason: null,
                cancellationToken)
                .ConfigureAwait(false);
            await UpdateCurrentAsync(
                connection,
                transaction,
                delivery.IdempotencyKey,
                SchedulerPulseDeliveryStatus.Fired,
                attemptCount: 1,
                delivery.OccurredAtUtc,
                reason: null,
                cancellationToken)
                .ConfigureAwait(false);
            await AppendHistoryAsync(
                connection,
                transaction,
                delivery.IdempotencyKey,
                sequence: 2,
                SchedulerPulseDeliveryStatus.Fired,
                delivery.OccurredAtUtc,
                reason: null,
                cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var nextAttempt = current.AttemptCount + 1;
            var nextSequence = await NextSequenceAsync(
                connection,
                transaction,
                delivery.IdempotencyKey,
                cancellationToken)
                .ConfigureAwait(false);
            await UpdateCurrentAsync(
                connection,
                transaction,
                delivery.IdempotencyKey,
                SchedulerPulseDeliveryStatus.Redelivered,
                nextAttempt,
                delivery.OccurredAtUtc,
                reason: null,
                cancellationToken)
                .ConfigureAwait(false);
            await AppendHistoryAsync(
                connection,
                transaction,
                delivery.IdempotencyKey,
                nextSequence,
                SchedulerPulseDeliveryStatus.Redelivered,
                delivery.OccurredAtUtc,
                reason: null,
                cancellationToken)
                .ConfigureAwait(false);
        }

        var updated = await FindAsync(
            connection,
            transaction,
            delivery.IdempotencyKey,
            cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated!;
    }

    public Task<SchedulerPulseDeliveryState> MarkDeliveredAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason = null,
        CancellationToken cancellationToken = default) =>
        MarkAsync(idempotencyKey, SchedulerPulseDeliveryStatus.Delivered, occurredAtUtc, reason, cancellationToken);

    public Task<SchedulerPulseDeliveryState> MarkSkippedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default) =>
        MarkAsync(
            idempotencyKey,
            SchedulerPulseDeliveryStatus.Skipped,
            occurredAtUtc,
            reason ?? throw new ArgumentNullException(nameof(reason)),
            cancellationToken);

    public Task<SchedulerPulseDeliveryState> MarkFailedAsync(
        PulseIdempotencyKey idempotencyKey,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason reason,
        CancellationToken cancellationToken = default) =>
        MarkAsync(
            idempotencyKey,
            SchedulerPulseDeliveryStatus.Failed,
            occurredAtUtc,
            reason ?? throw new ArgumentNullException(nameof(reason)),
            cancellationToken);

    public async Task<SchedulerPulseDeliveryState?> FindAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await FindAsync(connection, transaction: null, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SchedulerPulseDeliveryHistoryEntry>> ReadHistoryAsync(
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT sequence,
                   status,
                   occurred_at,
                   reason_code,
                   reason_message
            FROM {SchedulerPulseDeliverySchema.SchemaName}.pulse_delivery_history
            WHERE idempotency_key = @idempotency_key
            ORDER BY sequence;
            """,
            connection);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);

        var entries = new List<SchedulerPulseDeliveryHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SchedulerPulseDeliveryHistoryEntry(
                reader.GetInt32(0),
                ParseStatus(reader.GetString(1)),
                ReadUtc(reader, 2),
                ReadReason(reader, 3, 4)));
        }

        return entries;
    }

    private async Task<SchedulerPulseDeliveryState> MarkAsync(
        PulseIdempotencyKey idempotencyKey,
        SchedulerPulseDeliveryStatus status,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        RequireUtc(occurredAtUtc, nameof(occurredAtUtc));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await FindAsync(connection, transaction, idempotencyKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Scheduler pulse delivery '{idempotencyKey.Value}' does not exist.");
        var nextSequence = await NextSequenceAsync(connection, transaction, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);

        await UpdateCurrentAsync(
            connection,
            transaction,
            idempotencyKey,
            status,
            current.AttemptCount,
            occurredAtUtc,
            reason,
            cancellationToken)
            .ConfigureAwait(false);
        await AppendHistoryAsync(
            connection,
            transaction,
            idempotencyKey,
            nextSequence,
            status,
            occurredAtUtc,
            reason,
            cancellationToken)
            .ConfigureAwait(false);

        var updated = await FindAsync(connection, transaction, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated!;
    }

    private static async Task InsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SchedulerPulseDeliveryRecord delivery,
        SchedulerPulseDeliveryStatus status,
        int attemptCount,
        SchedulerPulseDeliveryReason? reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {SchedulerPulseDeliverySchema.SchemaName}.pulse_deliveries (
                idempotency_key,
                organization_id,
                position_id,
                schedule_id,
                window_start,
                window_end,
                message_id,
                thread_id,
                status,
                attempt_count,
                last_occurred_at,
                reason_code,
                reason_message,
                created_at,
                updated_at)
            VALUES (
                @idempotency_key,
                @organization_id,
                @position_id,
                @schedule_id,
                @window_start,
                @window_end,
                @message_id,
                @thread_id,
                @status,
                @attempt_count,
                @last_occurred_at,
                @reason_code,
                @reason_message,
                @created_at,
                @updated_at);
            """,
            connection,
            transaction);
        AddDeliveryParameters(command, delivery);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue("attempt_count", attemptCount);
        command.Parameters.AddWithValue("last_occurred_at", delivery.OccurredAtUtc);
        command.Parameters.AddWithValue("reason_code", (object?)reason?.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("reason_message", (object?)reason?.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", delivery.OccurredAtUtc);
        command.Parameters.AddWithValue("updated_at", delivery.OccurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PulseIdempotencyKey idempotencyKey,
        SchedulerPulseDeliveryStatus status,
        int attemptCount,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {SchedulerPulseDeliverySchema.SchemaName}.pulse_deliveries
            SET status = @status,
                attempt_count = @attempt_count,
                last_occurred_at = @last_occurred_at,
                reason_code = @reason_code,
                reason_message = @reason_message,
                updated_at = @updated_at
            WHERE idempotency_key = @idempotency_key;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue("attempt_count", attemptCount);
        command.Parameters.AddWithValue("last_occurred_at", occurredAtUtc);
        command.Parameters.AddWithValue("reason_code", (object?)reason?.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("reason_message", (object?)reason?.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("updated_at", occurredAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PulseIdempotencyKey idempotencyKey,
        int sequence,
        SchedulerPulseDeliveryStatus status,
        DateTimeOffset occurredAtUtc,
        SchedulerPulseDeliveryReason? reason,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {SchedulerPulseDeliverySchema.SchemaName}.pulse_delivery_history (
                idempotency_key,
                sequence,
                status,
                occurred_at,
                reason_code,
                reason_message)
            VALUES (
                @idempotency_key,
                @sequence,
                @status,
                @occurred_at,
                @reason_code,
                @reason_message);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);
        command.Parameters.AddWithValue("sequence", sequence);
        command.Parameters.AddWithValue("status", status.ToString());
        command.Parameters.AddWithValue("occurred_at", occurredAtUtc);
        command.Parameters.AddWithValue("reason_code", (object?)reason?.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("reason_message", (object?)reason?.Message ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> NextSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT COALESCE(MAX(sequence), 0) + 1
            FROM {SchedulerPulseDeliverySchema.SchemaName}.pulse_delivery_history
            WHERE idempotency_key = @idempotency_key;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<SchedulerPulseDeliveryState?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PulseIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        await using var command = new NpgsqlCommand(
            $"""
            SELECT organization_id,
                   position_id,
                   schedule_id,
                   window_start,
                   window_end,
                   message_id,
                   thread_id,
                   status,
                   attempt_count,
                   last_occurred_at,
                   reason_code,
                   reason_message
            FROM {SchedulerPulseDeliverySchema.SchemaName}.pulse_deliveries
            WHERE idempotency_key = @idempotency_key;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var window = TemporalWindow.From(ReadUtc(reader, 3), ReadUtc(reader, 4));
        var key = PulseIdempotencyKey.From(
            OrganizationId.From(reader.GetString(0)),
            PositionId.From(reader.GetString(1)),
            ScheduleId.From(reader.GetString(2)),
            window);

        return new SchedulerPulseDeliveryState(
            key,
            MessageId.From(reader.GetGuid(5)),
            ThreadId.From(reader.GetGuid(6)),
            ParseStatus(reader.GetString(7)),
            reader.GetInt32(8),
            ReadUtc(reader, 9),
            ReadReason(reader, 10, 11));
    }

    private static void AddDeliveryParameters(NpgsqlCommand command, SchedulerPulseDeliveryRecord delivery)
    {
        command.Parameters.AddWithValue("idempotency_key", delivery.IdempotencyKey.Value);
        command.Parameters.AddWithValue("organization_id", delivery.IdempotencyKey.Organization.Value);
        command.Parameters.AddWithValue("position_id", delivery.IdempotencyKey.Position.Value);
        command.Parameters.AddWithValue("schedule_id", delivery.IdempotencyKey.Schedule.Value);
        command.Parameters.AddWithValue("window_start", delivery.IdempotencyKey.Window.Start);
        command.Parameters.AddWithValue("window_end", delivery.IdempotencyKey.Window.End);
        command.Parameters.AddWithValue("message_id", delivery.MessageId.Value);
        command.Parameters.AddWithValue("thread_id", delivery.ThreadId.Value);
    }

    private static SchedulerPulseDeliveryStatus ParseStatus(string value) =>
        Enum.Parse<SchedulerPulseDeliveryStatus>(value, ignoreCase: false);

    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetFieldValue<DateTimeOffset>(ordinal);
        return value.ToUniversalTime();
    }

    private static SchedulerPulseDeliveryReason? ReadReason(
        NpgsqlDataReader reader,
        int codeOrdinal,
        int messageOrdinal)
    {
        if (reader.IsDBNull(codeOrdinal) || reader.IsDBNull(messageOrdinal))
        {
            return null;
        }

        return new SchedulerPulseDeliveryReason(
            reader.GetString(codeOrdinal),
            reader.GetString(messageOrdinal));
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Scheduler delivery timestamps must be expressed as UTC offsets.",
                parameterName);
        }
    }
}
