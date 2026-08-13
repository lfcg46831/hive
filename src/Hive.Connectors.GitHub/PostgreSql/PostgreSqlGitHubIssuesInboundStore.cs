using Npgsql;
using NpgsqlTypes;

namespace Hive.Connectors.GitHub.PostgreSql;

internal sealed class PostgreSqlGitHubIssuesInboundStore
    : IGitHubIssuesInboundStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlGitHubIssuesInboundStore(string connectionString)
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

    internal PostgreSqlGitHubIssuesInboundStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
        string instanceId,
        string repository,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(instanceId, repository);
        await using var command = _dataSource.CreateCommand(
            $"""
            SELECT cursor, not_before
            FROM {GitHubIssuesInboundSchema.SchemaName}.polling_checkpoints
            WHERE instance_id = @instance_id AND repository = @repository;
            """);
        AddIdentityParameters(command, instanceId, repository);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new GitHubIssuesPollingCheckpoint(
            instanceId,
            repository,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime());
    }

    public async Task<GitHubIssuesInboundCommitResult> CommitBatchAsync(
        GitHubIssuesPollingCheckpoint? expectedCheckpoint,
        GitHubIssuesInboundBatch batch,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset nextPollAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateCommit(expectedCheckpoint, batch, capturedAtUtc, nextPollAtUtc);
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@lock_key));",
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue(
                "lock_key",
                $"hive.github-issues:{batch.InstanceId}:{batch.Repository.ToLowerInvariant()}");
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var current = await ReadCheckpointAsync(
                connection,
                transaction,
                batch.InstanceId,
                batch.Repository,
                cancellationToken)
            .ConfigureAwait(false);
        if (current != expectedCheckpoint)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return GitHubIssuesInboundCommitResult.ConcurrentCheckpoint();
        }

        var inserted = 0;
        foreach (var inboundEvent in batch.Events)
        {
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {GitHubIssuesInboundSchema.SchemaName}.inbound_events (
                    instance_id,
                    repository,
                    external_event_id,
                    event_kind,
                    payload,
                    captured_at,
                    processing_state)
                VALUES (
                    @instance_id,
                    @repository,
                    @external_event_id,
                    @event_kind,
                    @payload,
                    @captured_at,
                    'pending')
                ON CONFLICT (instance_id, repository, external_event_id) DO NOTHING;
                """,
                connection,
                transaction);
            AddIdentityParameters(insert, batch.InstanceId, batch.Repository);
            insert.Parameters.AddWithValue("external_event_id", inboundEvent.ExternalEventId);
            insert.Parameters.AddWithValue("event_kind", inboundEvent.Kind);
            insert.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = inboundEvent.PayloadJson;
            insert.Parameters.AddWithValue("captured_at", capturedAtUtc);
            inserted += await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var checkpoint = new NpgsqlCommand(
            $"""
            INSERT INTO {GitHubIssuesInboundSchema.SchemaName}.polling_checkpoints (
                instance_id,
                repository,
                cursor,
                not_before,
                updated_at)
            VALUES (
                @instance_id,
                @repository,
                @cursor,
                @not_before,
                @updated_at)
            ON CONFLICT (instance_id, repository) DO UPDATE SET
                cursor = EXCLUDED.cursor,
                not_before = EXCLUDED.not_before,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction))
        {
            AddIdentityParameters(checkpoint, batch.InstanceId, batch.Repository);
            checkpoint.Parameters.Add("cursor", NpgsqlDbType.Text).Value =
                batch.NextCursor is { } cursor ? cursor : DBNull.Value;
            checkpoint.Parameters.AddWithValue("not_before", nextPollAtUtc);
            checkpoint.Parameters.AddWithValue("updated_at", capturedAtUtc);
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var committedCheckpoint = new GitHubIssuesPollingCheckpoint(
            batch.InstanceId,
            batch.Repository,
            batch.NextCursor,
            nextPollAtUtc);
        return new GitHubIssuesInboundCommitResult(true, inserted, committedCheckpoint);
    }

    public async Task<IReadOnlyList<GitHubIssuesInboundEnvelope>> ReadPendingAsync(
        string instanceId,
        string repository,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(instanceId, repository);
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        }

        await using var command = _dataSource.CreateCommand(
            $"""
            SELECT external_event_id, event_kind, payload::text, captured_at
            FROM {GitHubIssuesInboundSchema.SchemaName}.inbound_events
            WHERE instance_id = @instance_id
              AND repository = @repository
              AND processing_state = 'pending'
            ORDER BY captured_at, external_event_id
            LIMIT @limit;
            """);
        AddIdentityParameters(command, instanceId, repository);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new List<GitHubIssuesInboundEnvelope>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new GitHubIssuesInboundEnvelope(
                instanceId,
                repository,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3).ToUniversalTime()));
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<GitHubIssuesPollingCheckpoint?> ReadCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string instanceId,
        string repository,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT cursor, not_before
            FROM {GitHubIssuesInboundSchema.SchemaName}.polling_checkpoints
            WHERE instance_id = @instance_id AND repository = @repository;
            """,
            connection,
            transaction);
        AddIdentityParameters(command, instanceId, repository);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new GitHubIssuesPollingCheckpoint(
            instanceId,
            repository,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1).ToUniversalTime());
    }

    private static void ValidateCommit(
        GitHubIssuesPollingCheckpoint? expectedCheckpoint,
        GitHubIssuesInboundBatch batch,
        DateTimeOffset capturedAtUtc,
        DateTimeOffset nextPollAtUtc)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Capture timestamp must use a UTC offset.",
                nameof(capturedAtUtc));
        }

        if (nextPollAtUtc.Offset != TimeSpan.Zero || nextPollAtUtc < capturedAtUtc)
        {
            throw new ArgumentException(
                "Next-poll timestamp must use a UTC offset and not precede capture.",
                nameof(nextPollAtUtc));
        }

        if (expectedCheckpoint is not null
            && (!string.Equals(
                    expectedCheckpoint.InstanceId,
                    batch.InstanceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedCheckpoint.Repository,
                    batch.Repository,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Expected checkpoint belongs to a different GitHub connector source.",
                nameof(expectedCheckpoint));
        }
    }

    private static void ValidateIdentity(string instanceId, string repository)
    {
        GitHubIssuesConnectorInstanceConfiguration.RequireInstanceId(instanceId, nameof(instanceId));
        if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "Repository must be a trimmed 'owner/repository' identifier.",
                nameof(repository));
        }
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string instanceId,
        string repository)
    {
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("repository", repository.ToLowerInvariant());
    }
}
