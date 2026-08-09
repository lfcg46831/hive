using Hive.Infrastructure.Configuration;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

public sealed class PostgreSqlInboxProjectionFeed : IInboxProjectionFeed, IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly IInboxReadModelChangeSink _changeSink;

    public PostgreSqlInboxProjectionFeed(IConfiguration configuration)
        : this(ConnectionString(configuration), NoopInboxReadModelChangeSink.Instance)
    {
    }

    public PostgreSqlInboxProjectionFeed(
        IConfiguration configuration,
        IInboxReadModelChangeSink changeSink)
        : this(ConnectionString(configuration), changeSink)
    {
    }

    internal PostgreSqlInboxProjectionFeed(string? connectionString)
        : this(connectionString, NoopInboxReadModelChangeSink.Instance)
    {
    }

    internal PostgreSqlInboxProjectionFeed(
        string? connectionString,
        IInboxReadModelChangeSink changeSink)
    {
        _changeSink = changeSink ?? throw new ArgumentNullException(nameof(changeSink));
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _dataSource = NpgsqlDataSource.Create(connectionString);
        }
    }

    public bool IsConfigured => _dataSource is not null;

    public async ValueTask<long> ReadCheckpointAsync(
        InboxProjectionSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        RequireDefined(subscription, nameof(subscription));
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();

        await using var command = dataSource.CreateCommand(
            """
            SELECT source_offset
            FROM inbox.projection_checkpoints
            WHERE subscription = @subscription;
            """);
        AddText(command, "subscription", subscription.ToString());
        var checkpoint = await command.ExecuteScalarAsync(cancellationToken);
        return checkpoint is long value
            ? value
            : throw new InvalidOperationException(
                $"Inbox projection checkpoint '{subscription}' has not been initialized.");
    }

    public async ValueTask<bool> CapturePositionJournalAsync(
        long sourceOffset,
        IReadOnlyCollection<InboxProjectionFact> facts,
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

            if (fact.Source is not InboxProjectionSource.PositionEvent
                and not InboxProjectionSource.OrganizationalMessage)
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
            InboxProjectionSubscription.PositionJournal,
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
            InboxProjectionSubscription.PositionJournal,
            sourceOffset,
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask<bool> AdvancePositionJournalCheckpointAsync(
        long sourceOffset,
        CancellationToken cancellationToken = default)
    {
        if (sourceOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOffset),
                sourceOffset,
                "Projection source offset must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockFactCaptureAsync(connection, transaction, cancellationToken);
        var current = await LockCheckpointAsync(
            InboxProjectionSubscription.PositionJournal,
            connection,
            transaction,
            cancellationToken);
        if (sourceOffset <= current)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await AdvanceCheckpointAsync(
            InboxProjectionSubscription.PositionJournal,
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
            InboxProjectionSubscription.AuditLog,
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
                INSERT INTO inbox.projection_facts (
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
                throw new InvalidOperationException(
                    "Inbox audit projection batch did not return its checkpoint.");
            }

            captured = checked((int)reader.GetInt64(0));
            nextCheckpoint = reader.GetInt64(1);
        }

        if (nextCheckpoint > checkpoint)
        {
            await AdvanceCheckpointAsync(
                InboxProjectionSubscription.AuditLog,
                nextCheckpoint,
                connection,
                transaction,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return captured;
    }

    public async ValueTask<InboxProjectionProgress> ReadProjectionProgressAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT sequence_id,
                   last_event_applied_at_utc
            FROM inbox.projection_progress
            WHERE projection = 'Inbox';
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The inbox projection progress row has not been initialized.");
        }

        return ReadProgress(reader);
    }

    public async ValueTask<IReadOnlyList<InboxProjectionFactItem>> ReadProjectionFactsAsync(
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
            FROM inbox.projection_facts
            WHERE sequence_id > @after_sequence_id
            ORDER BY sequence_id
            LIMIT @batch_size;
            """);
        command.Parameters.Add("after_sequence_id", NpgsqlDbType.Bigint).Value = afterSequenceId;
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = batchSize;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<InboxProjectionFactItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var fact = new InboxProjectionFact(
                ParseSource(reader.GetString(1)),
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
            items.Add(new InboxProjectionFactItem(reader.GetInt64(0), fact));
        }

        return items;
    }

    public async ValueTask<bool> ApplyProjectionFactAsync(
        InboxProjectionFactItem item,
        IReadOnlyCollection<InboxProjectionChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateChanges(changes, item.Fact.OrganizationId);
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var progress = await LockProjectionProgressAsync(connection, transaction, cancellationToken);
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
                $"Inbox projection fact {item.SequenceId} cannot be applied after " +
                $"{progress.LastAppliedSequenceId}; the next durable fact is " +
                $"{nextSequenceId?.ToString() ?? "missing"}.");
        }

        var appliedChanges = new List<InboxProjectionChange>(changes.Count);
        foreach (var change in changes)
        {
            if (await ApplyItemChangeAsync(change, connection, transaction, cancellationToken) > 0)
            {
                appliedChanges.Add(change);
            }
        }

        await AdvanceProjectionWatermarkAsync(item, connection, transaction, cancellationToken);
        await AdvanceProjectionProgressAsync(item, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var change in appliedChanges)
        {
            await _changeSink.ProjectionChangedAsync(change, cancellationToken);
        }

        return true;
    }

    public async ValueTask<int> ApplyProjectionChangesAsync(
        long expectedProjectionSequence,
        IReadOnlyCollection<InboxProjectionChange> changes,
        CancellationToken cancellationToken = default)
    {
        if (expectedProjectionSequence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedProjectionSequence),
                expectedProjectionSequence,
                "Expected projection sequence cannot be negative.");
        }

        ValidateChanges(changes, organizationId: null);
        if (changes.Count == 0)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = RequireDataSource();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var progress = await LockProjectionProgressAsync(connection, transaction, cancellationToken);
        if (progress.LastAppliedSequenceId != expectedProjectionSequence)
        {
            throw new InvalidOperationException(
                $"Inbox projection changes mapped at sequence {expectedProjectionSequence} cannot " +
                $"be applied after durable sequence {progress.LastAppliedSequenceId}.");
        }

        var appliedChanges = new List<InboxProjectionChange>(changes.Count);
        foreach (var change in changes)
        {
            if (await ApplyItemChangeAsync(
                    change,
                    connection,
                    transaction,
                    cancellationToken) > 0)
            {
                appliedChanges.Add(change);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var change in appliedChanges)
        {
            await _changeSink.ProjectionChangedAsync(change, cancellationToken);
        }

        return appliedChanges.Count;
    }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();

    private static async Task<InboxProjectionProgress> LockProjectionProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT sequence_id,
                   last_event_applied_at_utc
            FROM inbox.projection_progress
            WHERE projection = 'Inbox'
            FOR UPDATE;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The inbox projection progress row has not been initialized.");
        }

        return ReadProgress(reader);
    }

    private static InboxProjectionProgress ReadProgress(NpgsqlDataReader reader) =>
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
            FROM inbox.projection_facts
            WHERE sequence_id > @after_sequence_id
            ORDER BY sequence_id
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.Add("after_sequence_id", NpgsqlDbType.Bigint).Value = afterSequenceId;
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    private static async Task<int> ApplyItemChangeAsync(
        InboxProjectionChange change,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var item = change.Item;
        var origin = Endpoint(item.Origin);
        var destination = Endpoint(item.Destination);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inbox.items (
                organization_id,
                assigned_position_id,
                message_id,
                message_type,
                origin_type,
                origin_position_id,
                destination_type,
                destination_position_id,
                thread_id,
                priority,
                sent_at_utc,
                deadline_at_utc,
                is_expired,
                is_delegated,
                last_reminder_at_utc,
                response_state,
                approval_request_id,
                approval_action,
                approval_policy_ref,
                approval_state,
                approval_decision_message_id,
                approval_decided_at_utc,
                last_fact_type,
                last_changed_at_utc)
            VALUES (
                @organization_id,
                @assigned_position_id,
                @message_id,
                @message_type,
                @origin_type,
                @origin_position_id,
                @destination_type,
                @destination_position_id,
                @thread_id,
                @priority,
                @sent_at_utc,
                @deadline_at_utc,
                @is_expired,
                @is_delegated,
                @last_reminder_at_utc,
                @response_state,
                @approval_request_id,
                @approval_action,
                @approval_policy_ref,
                @approval_state,
                @approval_decision_message_id,
                @approval_decided_at_utc,
                @last_fact_type,
                @last_changed_at_utc)
            ON CONFLICT (organization_id, assigned_position_id, message_id) DO UPDATE SET
                message_type = EXCLUDED.message_type,
                origin_type = EXCLUDED.origin_type,
                origin_position_id = EXCLUDED.origin_position_id,
                destination_type = EXCLUDED.destination_type,
                destination_position_id = EXCLUDED.destination_position_id,
                thread_id = EXCLUDED.thread_id,
                priority = EXCLUDED.priority,
                sent_at_utc = EXCLUDED.sent_at_utc,
                deadline_at_utc = EXCLUDED.deadline_at_utc,
                is_expired = EXCLUDED.is_expired,
                is_delegated = EXCLUDED.is_delegated,
                last_reminder_at_utc = EXCLUDED.last_reminder_at_utc,
                response_state = EXCLUDED.response_state,
                approval_request_id = EXCLUDED.approval_request_id,
                approval_action = EXCLUDED.approval_action,
                approval_policy_ref = EXCLUDED.approval_policy_ref,
                approval_state = EXCLUDED.approval_state,
                approval_decision_message_id = EXCLUDED.approval_decision_message_id,
                approval_decided_at_utc = EXCLUDED.approval_decided_at_utc,
                last_fact_type = EXCLUDED.last_fact_type,
                last_changed_at_utc = EXCLUDED.last_changed_at_utc
            WHERE ROW(
                    inbox.items.message_type,
                    inbox.items.origin_type,
                    inbox.items.origin_position_id,
                    inbox.items.destination_type,
                    inbox.items.destination_position_id,
                    inbox.items.thread_id,
                    inbox.items.priority,
                    inbox.items.sent_at_utc,
                    inbox.items.deadline_at_utc,
                    inbox.items.is_expired,
                    inbox.items.is_delegated,
                    inbox.items.last_reminder_at_utc,
                    inbox.items.response_state,
                    inbox.items.approval_request_id,
                    inbox.items.approval_action,
                    inbox.items.approval_policy_ref,
                    inbox.items.approval_state,
                    inbox.items.approval_decision_message_id,
                    inbox.items.approval_decided_at_utc)
                IS DISTINCT FROM ROW(
                    EXCLUDED.message_type,
                    EXCLUDED.origin_type,
                    EXCLUDED.origin_position_id,
                    EXCLUDED.destination_type,
                    EXCLUDED.destination_position_id,
                    EXCLUDED.thread_id,
                    EXCLUDED.priority,
                    EXCLUDED.sent_at_utc,
                    EXCLUDED.deadline_at_utc,
                    EXCLUDED.is_expired,
                    EXCLUDED.is_delegated,
                    EXCLUDED.last_reminder_at_utc,
                    EXCLUDED.response_state,
                    EXCLUDED.approval_request_id,
                    EXCLUDED.approval_action,
                    EXCLUDED.approval_policy_ref,
                    EXCLUDED.approval_state,
                    EXCLUDED.approval_decision_message_id,
                    EXCLUDED.approval_decided_at_utc);
            """,
            connection,
            transaction);
        AddText(command, "organization_id", item.Key.OrganizationId.Value);
        AddText(command, "assigned_position_id", item.Key.AssignedPositionId.Value);
        command.Parameters.Add("message_id", NpgsqlDbType.Uuid).Value = item.Key.MessageId.Value;
        AddText(command, "message_type", item.Type.ToString());
        AddText(command, "origin_type", origin.Type);
        AddNullableText(command, "origin_position_id", origin.PositionId);
        AddText(command, "destination_type", destination.Type);
        AddNullableText(command, "destination_position_id", destination.PositionId);
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = item.ThreadId.Value;
        AddText(command, "priority", item.Priority.ToString());
        command.Parameters.Add("sent_at_utc", NpgsqlDbType.TimestampTz).Value = item.SentAtUtc;
        command.Parameters.Add("deadline_at_utc", NpgsqlDbType.TimestampTz).Value =
            item.DeadlineAtUtc ?? (object)DBNull.Value;
        command.Parameters.Add("is_expired", NpgsqlDbType.Boolean).Value = item.IsExpired;
        command.Parameters.Add("is_delegated", NpgsqlDbType.Boolean).Value = item.IsDelegated;
        command.Parameters.Add("last_reminder_at_utc", NpgsqlDbType.TimestampTz).Value =
            item.LastReminderAtUtc ?? (object)DBNull.Value;
        AddText(command, "response_state", item.ResponseState.ToString());
        command.Parameters.Add("approval_request_id", NpgsqlDbType.Uuid).Value =
            item.Approval?.RequestId.Value ?? (object)DBNull.Value;
        AddNullableText(command, "approval_action", item.Approval?.Action);
        AddNullableText(command, "approval_policy_ref", item.Approval?.Policy.Value);
        AddNullableText(command, "approval_state", item.Approval?.State.ToString());
        command.Parameters.Add("approval_decision_message_id", NpgsqlDbType.Uuid).Value =
            item.Approval?.DecisionMessageId?.Value ?? (object)DBNull.Value;
        command.Parameters.Add("approval_decided_at_utc", NpgsqlDbType.TimestampTz).Value =
            item.Approval?.DecidedAtUtc ?? (object)DBNull.Value;
        AddText(command, "last_fact_type", change.FactType);
        command.Parameters.Add("last_changed_at_utc", NpgsqlDbType.TimestampTz).Value =
            change.OccurredAtUtc;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdvanceProjectionWatermarkAsync(
        InboxProjectionFactItem item,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inbox.projection_watermarks (
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
            WHERE inbox.projection_watermarks.sequence_id < EXCLUDED.sequence_id;
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
        InboxProjectionFactItem item,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE inbox.projection_progress
            SET sequence_id = @sequence_id,
                last_event_applied_at_utc = @last_event_applied_at_utc,
                updated_at_utc = CURRENT_TIMESTAMP
            WHERE projection = 'Inbox';
            """,
            connection,
            transaction);
        command.Parameters.Add("sequence_id", NpgsqlDbType.Bigint).Value = item.SequenceId;
        command.Parameters.Add("last_event_applied_at_utc", NpgsqlDbType.TimestampTz).Value =
            item.Fact.OccurredAtUtc;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The inbox projection progress could not be advanced.");
        }
    }

    private static void ValidateChanges(
        IReadOnlyCollection<InboxProjectionChange> changes,
        OrganizationId? organizationId)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            ArgumentNullException.ThrowIfNull(change.Item);
            if (organizationId is not null && change.Item.Key.OrganizationId != organizationId)
            {
                throw new ArgumentException(
                    "Every inbox change must belong to the organization of the applied fact.",
                    nameof(changes));
            }
        }
    }

    private static (string Type, string? PositionId) Endpoint(EndpointRef endpoint) =>
        endpoint switch
        {
            PositionEndpointRef position => ("Position", position.PositionId.Value),
            OrganizationOwnerEndpointRef => ("OrganizationOwner", null),
            _ => throw new ArgumentException(
                $"Inbox endpoint '{endpoint.GetType().Name}' cannot be materialized.",
                nameof(endpoint)),
        };

    private static InboxProjectionSource ParseSource(string value) =>
        Enum.TryParse<InboxProjectionSource>(value, ignoreCase: false, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Unknown materialized inbox projection source '{value}'.");

    private static async Task LockFactCaptureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_advisory_xact_lock(hashtext('hive.inbox.projection-facts'));
            """,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> LockCheckpointAsync(
        InboxProjectionSubscription subscription,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT source_offset
            FROM inbox.projection_checkpoints
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
                $"Inbox projection checkpoint '{subscription}' has not been initialized.");
    }

    private static async Task InsertFactAsync(
        InboxProjectionFact fact,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO inbox.projection_facts (
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
        InboxProjectionSubscription subscription,
        long sourceOffset,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE inbox.projection_checkpoints
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
                $"Inbox projection checkpoint '{subscription}' could not be advanced.");
        }
    }

    private NpgsqlDataSource RequireDataSource() =>
        _dataSource
        ?? throw new InvalidOperationException("The inbox projection feed is not configured.");

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
