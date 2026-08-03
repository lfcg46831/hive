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

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

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

    private static string? ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
    }
}
