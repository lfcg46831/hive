using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Organization.ReadModels.PostgreSql;

public sealed class PostgreSqlPositionLiveStateProjectionFeed :
    IPositionLiveStateProjectionFeed,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public PostgreSqlPositionLiveStateProjectionFeed(IConfiguration configuration)
        : this(ConnectionString(configuration))
    {
    }

    internal PostgreSqlPositionLiveStateProjectionFeed(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }
    }

    public bool IsConfigured => _dataSource is not null;

    public async ValueTask<long> ReadCheckpointAsync(
        PositionLiveStateProjectionSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        RequireDefined(subscription, nameof(subscription));
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();

        await using var command = dataSource.CreateCommand(
            """
            SELECT source_offset
            FROM organogram.position_state_projection_checkpoints
            WHERE subscription = @subscription;
            """);
        AddText(command, "subscription", subscription.ToString());
        var checkpoint = await command.ExecuteScalarAsync(cancellationToken);
        return checkpoint is long value
            ? value
            : throw new InvalidOperationException(
                $"Projection checkpoint '{subscription}' has not been initialized.");
    }

    public async ValueTask<bool> CapturePositionJournalAsync(
        long sourceOffset,
        IReadOnlyCollection<PositionLiveStateProjectionFact> facts,
        CancellationToken cancellationToken = default)
    {
        if (sourceOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOffset),
                sourceOffset,
                "Projection source offset must be positive.");
        }

        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0)
        {
            throw new ArgumentException(
                "A journal checkpoint cannot advance without at least one captured fact.",
                nameof(facts));
        }

        foreach (var fact in facts)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (fact.SourceOffset != sourceOffset)
            {
                throw new ArgumentException(
                    "Every captured fact must have the journal source offset being checkpointed.",
                    nameof(facts));
            }

            if (fact.Source is not PositionLiveStateProjectionSource.PositionEvent
                and not PositionLiveStateProjectionSource.OrganizationalMessage)
            {
                throw new ArgumentException(
                    "Only position events and organizational messages belong to the position journal subscription.",
                    nameof(facts));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFactCaptureAsync(connection, transaction, cancellationToken);
        var current = await LockCheckpointAsync(
            PositionLiveStateProjectionSubscription.PositionJournal,
            connection,
            transaction,
            cancellationToken);
        if (sourceOffset <= current)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        foreach (var fact in facts)
        {
            await InsertFactAsync(fact, connection, transaction, cancellationToken);
        }

        await AdvanceCheckpointAsync(
            PositionLiveStateProjectionSubscription.PositionJournal,
            sourceOffset,
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask<int> CaptureAuditLogBatchAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                "Projection batch size must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFactCaptureAsync(connection, transaction, cancellationToken);
        var checkpoint = await LockCheckpointAsync(
            PositionLiveStateProjectionSubscription.AuditLog,
            connection,
            transaction,
            cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            WITH next_events AS MATERIALIZED (
                SELECT event.*
                FROM audit.journey_events event
                WHERE event.sequence_id > @checkpoint
                ORDER BY event.sequence_id
                LIMIT @batch_size
            ),
            captured AS (
                INSERT INTO organogram.position_state_projection_facts (
                    source,
                    source_offset,
                    organization_id,
                    position_id,
                    fact_type,
                    message_id,
                    thread_id,
                    occurred_at_utc,
                    payload)
                SELECT 'AuditLog',
                       event.sequence_id,
                       event.organization_id,
                       event.position_id,
                       event.stage,
                       event.message_id,
                       event.thread_id,
                       event.occurred_at_utc,
                       to_jsonb(event)
                FROM next_events event
                ON CONFLICT (source, source_offset) DO NOTHING
                RETURNING source_offset
            )
            SELECT count(*), COALESCE(max(sequence_id), @checkpoint)
            FROM next_events;
            """,
            connection,
            transaction);
        command.Parameters.Add("checkpoint", NpgsqlDbType.Bigint).Value = checkpoint;
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = batchSize;

        int captured;
        long nextCheckpoint;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Audit projection batch did not return its checkpoint.");
            }

            captured = checked((int)reader.GetInt64(0));
            nextCheckpoint = reader.GetInt64(1);
        }

        if (nextCheckpoint > checkpoint)
        {
            await AdvanceCheckpointAsync(
                PositionLiveStateProjectionSubscription.AuditLog,
                nextCheckpoint,
                connection,
                transaction,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return captured;
    }

    public async ValueTask<PositionLiveStateProjectionProgress> ReadProjectionProgressAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT sequence_id,
                   last_event_applied_at_utc
            FROM organogram.position_state_projection_progress
            WHERE projection = 'LiveState';
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The live-state projection progress row has not been initialized.");
        }

        return ReadProgress(reader);
    }

    public async ValueTask<IReadOnlyList<PositionLiveStateProjectionItem>> ReadProjectionFactsAsync(
        long afterSequenceId,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (afterSequenceId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterSequenceId),
                afterSequenceId,
                "Projection sequence cannot be negative.");
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                batchSize,
                "Projection batch size must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT sequence_id,
                   source,
                   source_offset,
                   persistence_id,
                   persistence_sequence,
                   organization_id,
                   position_id,
                   fact_type,
                   message_id,
                   thread_id,
                   occurred_at_utc,
                   payload::text
            FROM organogram.position_state_projection_facts
            WHERE sequence_id > @after_sequence_id
            ORDER BY sequence_id
            LIMIT @batch_size;
            """);
        command.Parameters.Add("after_sequence_id", NpgsqlDbType.Bigint).Value = afterSequenceId;
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = batchSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PositionLiveStateProjectionItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = ParseSource(reader.GetString(1));
            var fact = new PositionLiveStateProjectionFact(
                source,
                reader.GetInt64(2),
                OrganizationId.From(reader.GetString(5)),
                reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(10).ToUniversalTime(),
                reader.GetString(11),
                reader.IsDBNull(6) ? null : PositionId.From(reader.GetString(6)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(8) ? null : MessageId.From(reader.GetGuid(8)),
                reader.IsDBNull(9) ? null : ThreadId.From(reader.GetGuid(9)));
            items.Add(new PositionLiveStateProjectionItem(reader.GetInt64(0), fact));
        }

        return items;
    }

    public async ValueTask<bool> ApplyProjectionFactAsync(
        PositionLiveStateProjectionItem item,
        PositionLiveStateProjectionUpdate? update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (update is not null && update.OrganizationId != item.Fact.OrganizationId)
        {
            throw new ArgumentException(
                "A live-state update must belong to the organization of the applied fact.",
                nameof(update));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var progress = await LockProjectionProgressAsync(
            connection,
            transaction,
            cancellationToken);
        if (item.SequenceId <= progress.LastAppliedSequenceId)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var nextSequenceId = await ReadNextProjectionFactSequenceAsync(
            progress.LastAppliedSequenceId,
            connection,
            transaction,
            cancellationToken);
        if (nextSequenceId != item.SequenceId)
        {
            throw new InvalidOperationException(
                $"Projection fact {item.SequenceId} cannot be applied after " +
                $"{progress.LastAppliedSequenceId}; the next durable fact is {nextSequenceId?.ToString() ?? "missing"}.");
        }

        if (update is not null)
        {
            await ApplyStateUpdateAsync(update, connection, transaction, cancellationToken);
        }

        await AdvanceProjectionWatermarkAsync(item, connection, transaction, cancellationToken);
        await AdvanceProjectionProgressAsync(item, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

    private static async Task<PositionLiveStateProjectionProgress> LockProjectionProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT sequence_id,
                   last_event_applied_at_utc
            FROM organogram.position_state_projection_progress
            WHERE projection = 'LiveState'
            FOR UPDATE;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The live-state projection progress row has not been initialized.");
        }

        return ReadProgress(reader);
    }

    private static PositionLiveStateProjectionProgress ReadProgress(NpgsqlDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.IsDBNull(1)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime());

    private static async Task<long?> ReadNextProjectionFactSequenceAsync(
        long afterSequenceId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT sequence_id
            FROM organogram.position_state_projection_facts
            WHERE sequence_id > @after_sequence_id
            ORDER BY sequence_id
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.Add("after_sequence_id", NpgsqlDbType.Bigint).Value = afterSequenceId;
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    private static async Task ApplyStateUpdateAsync(
        PositionLiveStateProjectionUpdate update,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE organogram.position_states
            SET state = @state,
                sequence = sequence + 1,
                updated_at_utc = @updated_at_utc,
                last_event_type = CASE
                    WHEN @has_correlated_event THEN @last_event_type
                    ELSE last_event_type
                END,
                last_event_thread_id = CASE
                    WHEN @has_correlated_event THEN @last_event_thread_id
                    ELSE last_event_thread_id
                END,
                last_event_occurred_at_utc = CASE
                    WHEN @has_correlated_event THEN @last_event_occurred_at_utc
                    ELSE last_event_occurred_at_utc
                END
            WHERE organization_id = @organization_id
              AND position_id = @position_id;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", update.OrganizationId.Value);
        AddText(command, "position_id", update.PositionId.Value);
        AddText(command, "state", update.State.ToString());
        command.Parameters.Add("updated_at_utc", NpgsqlDbType.TimestampTz).Value = update.UpdatedAtUtc;
        command.Parameters.Add("has_correlated_event", NpgsqlDbType.Boolean).Value =
            update.CorrelatedEvent is not null;
        AddNullableText(command, "last_event_type", update.CorrelatedEvent?.Type);
        command.Parameters.Add("last_event_thread_id", NpgsqlDbType.Uuid).Value =
            update.CorrelatedEvent?.ThreadId ?? (object)DBNull.Value;
        command.Parameters.Add("last_event_occurred_at_utc", NpgsqlDbType.TimestampTz).Value =
            update.CorrelatedEvent?.OccurredAtUtc ?? (object)DBNull.Value;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Position '{update.PositionId.Value}' does not have a live-state row in " +
                $"organization '{update.OrganizationId.Value}'.");
        }
    }

    private static async Task AdvanceProjectionWatermarkAsync(
        PositionLiveStateProjectionItem item,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organogram.position_state_projection_watermarks (
                organization_id,
                sequence_id,
                last_event_applied_at_utc,
                updated_at_utc)
            VALUES (
                @organization_id,
                @sequence_id,
                @last_event_applied_at_utc,
                CURRENT_TIMESTAMP)
            ON CONFLICT (organization_id) DO UPDATE SET
                sequence_id = EXCLUDED.sequence_id,
                last_event_applied_at_utc = EXCLUDED.last_event_applied_at_utc,
                updated_at_utc = CURRENT_TIMESTAMP
            WHERE organogram.position_state_projection_watermarks.sequence_id < EXCLUDED.sequence_id;
            """,
            connection,
            transaction);
        AddText(command, "organization_id", item.Fact.OrganizationId.Value);
        command.Parameters.Add("sequence_id", NpgsqlDbType.Bigint).Value = item.SequenceId;
        command.Parameters.Add("last_event_applied_at_utc", NpgsqlDbType.TimestampTz).Value =
            item.Fact.OccurredAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdvanceProjectionProgressAsync(
        PositionLiveStateProjectionItem item,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE organogram.position_state_projection_progress
            SET sequence_id = @sequence_id,
                last_event_applied_at_utc = @last_event_applied_at_utc,
                updated_at_utc = CURRENT_TIMESTAMP
            WHERE projection = 'LiveState';
            """,
            connection,
            transaction);
        command.Parameters.Add("sequence_id", NpgsqlDbType.Bigint).Value = item.SequenceId;
        command.Parameters.Add("last_event_applied_at_utc", NpgsqlDbType.TimestampTz).Value =
            item.Fact.OccurredAtUtc;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The live-state projection progress could not be advanced.");
        }
    }

    private static async Task<long> LockCheckpointAsync(
        PositionLiveStateProjectionSubscription subscription,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT source_offset
            FROM organogram.position_state_projection_checkpoints
            WHERE subscription = @subscription
            FOR UPDATE;
            """,
            connection,
            transaction);
        AddText(command, "subscription", subscription.ToString());
        var checkpoint = await command.ExecuteScalarAsync(cancellationToken);
        return checkpoint is long value
            ? value
            : throw new InvalidOperationException(
                $"Projection checkpoint '{subscription}' has not been initialized.");
    }

    private static async Task LockFactCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_advisory_xact_lock(
                hashtext('hive.organogram.position-live-state-projection-facts'));
            """,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFactAsync(
        PositionLiveStateProjectionFact fact,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organogram.position_state_projection_facts (
                source,
                source_offset,
                persistence_id,
                persistence_sequence,
                organization_id,
                position_id,
                fact_type,
                message_id,
                thread_id,
                occurred_at_utc,
                payload)
            VALUES (
                @source,
                @source_offset,
                @persistence_id,
                @persistence_sequence,
                @organization_id,
                @position_id,
                @fact_type,
                @message_id,
                @thread_id,
                @occurred_at_utc,
                @payload)
            ON CONFLICT (source, source_offset) DO NOTHING;
            """,
            connection,
            transaction);
        AddText(command, "source", fact.Source.ToString());
        command.Parameters.Add("source_offset", NpgsqlDbType.Bigint).Value = fact.SourceOffset;
        AddNullableText(command, "persistence_id", fact.PersistenceId);
        command.Parameters.Add("persistence_sequence", NpgsqlDbType.Bigint).Value =
            fact.PersistenceSequence ?? (object)DBNull.Value;
        AddText(command, "organization_id", fact.OrganizationId.Value);
        AddNullableText(command, "position_id", fact.PositionId?.Value);
        AddText(command, "fact_type", fact.FactType);
        command.Parameters.Add("message_id", NpgsqlDbType.Uuid).Value =
            fact.MessageId?.Value ?? (object)DBNull.Value;
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value =
            fact.ThreadId?.Value ?? (object)DBNull.Value;
        command.Parameters.Add("occurred_at_utc", NpgsqlDbType.TimestampTz).Value = fact.OccurredAtUtc;
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = fact.PayloadJson;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdvanceCheckpointAsync(
        PositionLiveStateProjectionSubscription subscription,
        long sourceOffset,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE organogram.position_state_projection_checkpoints
            SET source_offset = @source_offset,
                updated_at_utc = CURRENT_TIMESTAMP
            WHERE subscription = @subscription;
            """,
            connection,
            transaction);
        command.Parameters.Add("source_offset", NpgsqlDbType.Bigint).Value = sourceOffset;
        AddText(command, "subscription", subscription.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Projection checkpoint '{subscription}' could not be advanced.");
        }
    }

    private NpgsqlDataSource RequireDataSource() =>
        _dataSource
        ?? throw new InvalidOperationException("The position live-state projection feed is not configured.");

    private static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Unknown {typeof(TEnum).Name}.");
        }
    }

    private static void AddText(NpgsqlCommand command, string name, string value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value;

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = value ?? (object)DBNull.Value;

    private static PositionLiveStateProjectionSource ParseSource(string value) =>
        Enum.TryParse<PositionLiveStateProjectionSource>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Unknown materialized live-state projection source '{value}'.");

    private static string? ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
    }
}
