using Npgsql;
using NpgsqlTypes;
using Hive.Domain.Identity;

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

    public async Task<bool> TryCompleteAsync(
        GitHubIssuesInboundEnvelope envelope,
        GitHubIssuesInboundCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(completion);
        ValidateIdentity(envelope.InstanceId, envelope.Repository);
        if (completion.State is GitHubIssuesInboundCompletionState.Submitted)
        {
            return await TryCompleteSubmittedAsync(
                    envelope,
                    completion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var command = _dataSource.CreateCommand(
            $"""
            UPDATE {GitHubIssuesInboundSchema.SchemaName}.inbound_events
            SET processing_state = @processing_state,
                processed_at = @processed_at,
                rejection_code = @rejection_code
            WHERE instance_id = @instance_id
              AND repository = @repository
              AND external_event_id = @external_event_id
              AND processing_state = 'pending';
            """);
        AddIdentityParameters(command, envelope.InstanceId, envelope.Repository);
        command.Parameters.AddWithValue("external_event_id", envelope.ExternalEventId);
        command.Parameters.AddWithValue("processing_state", "rejected");
        command.Parameters.AddWithValue("processed_at", completion.CompletedAtUtc);
        command.Parameters.Add("rejection_code", NpgsqlDbType.Text).Value =
            completion.ReasonCode is { } reasonCode ? reasonCode : DBNull.Value;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public ValueTask<GitHubIssueCorrelation?> FindCorrelationByIssueAsync(
        string instanceId,
        OrganizationId organizationId,
        string repository,
        long issueNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelationIdentity(instanceId, organizationId, repository, issueNumber);
        var command = _dataSource.CreateCommand(
            $"""
            SELECT instance_id, organization_id, repository, issue_number, thread_id,
                   root_directive_id
            FROM {GitHubIssuesInboundSchema.SchemaName}.issue_correlations
            WHERE instance_id = @instance_id
              AND organization_id = @organization_id
              AND repository = @repository
              AND issue_number = @issue_number;
            """);
        AddCorrelationIdentityParameters(
            command,
            instanceId,
            organizationId,
            repository,
            issueNumber);
        return ReadCorrelationAsync(command, cancellationToken);
    }

    public ValueTask<GitHubIssueCorrelation?> FindCorrelationByThreadAsync(
        string instanceId,
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelationScope(instanceId, organizationId);
        ArgumentNullException.ThrowIfNull(threadId);
        var command = _dataSource.CreateCommand(
            $"""
            SELECT instance_id, organization_id, repository, issue_number, thread_id,
                   root_directive_id
            FROM {GitHubIssuesInboundSchema.SchemaName}.issue_correlations
            WHERE instance_id = @instance_id
              AND organization_id = @organization_id
              AND thread_id = @thread_id;
            """);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("organization_id", organizationId.Value);
        command.Parameters.AddWithValue("thread_id", threadId.Value);
        return ReadCorrelationAsync(command, cancellationToken);
    }

    public ValueTask<GitHubIssueCorrelation?> FindCorrelationByDirectiveAsync(
        string instanceId,
        OrganizationId organizationId,
        DirectiveId directiveId,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelationScope(instanceId, organizationId);
        ArgumentNullException.ThrowIfNull(directiveId);
        var command = _dataSource.CreateCommand(
            $"""
            SELECT issue.instance_id, issue.organization_id, issue.repository,
                   issue.issue_number, issue.thread_id, issue.root_directive_id
            FROM {GitHubIssuesInboundSchema.SchemaName}.issue_directive_correlations AS directive
            INNER JOIN {GitHubIssuesInboundSchema.SchemaName}.issue_correlations AS issue
                ON issue.instance_id = directive.instance_id
               AND issue.repository = directive.repository
               AND issue.issue_number = directive.issue_number
            WHERE issue.instance_id = @instance_id
              AND issue.organization_id = @organization_id
              AND directive.directive_id = @directive_id;
            """);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("organization_id", organizationId.Value);
        command.Parameters.AddWithValue("directive_id", directiveId.Value);
        return ReadCorrelationAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> TryCompleteSubmittedAsync(
        GitHubIssuesInboundEnvelope envelope,
        GitHubIssuesInboundCompletion completion,
        CancellationToken cancellationToken)
    {
        var submission = completion.Submission
            ?? throw new ArgumentException(
                "Submitted completion requires issue correlation.",
                nameof(completion));
        var correlation = submission.Issue;
        if (!string.Equals(
                correlation.InstanceId,
                envelope.InstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                correlation.Repository,
                envelope.Repository,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Submitted correlation belongs to a different staged source.",
                nameof(completion));
        }

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var staged = new NpgsqlCommand(
            $"""
            SELECT processing_state
            FROM {GitHubIssuesInboundSchema.SchemaName}.inbound_events
            WHERE instance_id = @instance_id
              AND repository = @repository
              AND external_event_id = @external_event_id
            FOR UPDATE;
            """,
            connection,
            transaction))
        {
            AddIdentityParameters(staged, envelope.InstanceId, envelope.Repository);
            staged.Parameters.AddWithValue("external_event_id", envelope.ExternalEventId);
            var state = await staged.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(state as string, "pending", StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var issueLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext(@lock_key));",
            connection,
            transaction))
        {
            issueLock.Parameters.AddWithValue(
                "lock_key",
                $"hive.github-issue:{correlation.InstanceId}:{correlation.Repository}:{correlation.IssueNumber}");
            await issueLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertOrVerifyIssueCorrelationAsync(
                connection,
                transaction,
                correlation,
                completion.CompletedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        await InsertOrVerifyDirectiveCorrelationAsync(
                connection,
                transaction,
                envelope,
                correlation,
                submission.DirectiveId,
                completion.CompletedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        await using (var complete = new NpgsqlCommand(
            $"""
            UPDATE {GitHubIssuesInboundSchema.SchemaName}.inbound_events
            SET processing_state = 'submitted',
                processed_at = @processed_at,
                rejection_code = NULL
            WHERE instance_id = @instance_id
              AND repository = @repository
              AND external_event_id = @external_event_id
              AND processing_state = 'pending';
            """,
            connection,
            transaction))
        {
            AddIdentityParameters(complete, envelope.InstanceId, envelope.Repository);
            complete.Parameters.AddWithValue("external_event_id", envelope.ExternalEventId);
            complete.Parameters.AddWithValue("processed_at", completion.CompletedAtUtc);
            if (await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "Staged GitHub event changed while its correlation was being committed.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task InsertOrVerifyIssueCorrelationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GitHubIssueCorrelation correlation,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {GitHubIssuesInboundSchema.SchemaName}.issue_correlations (
                instance_id,
                organization_id,
                repository,
                issue_number,
                thread_id,
                root_directive_id,
                created_at)
            VALUES (
                @instance_id,
                @organization_id,
                @repository,
                @issue_number,
                @thread_id,
                @root_directive_id,
                @created_at)
            ON CONFLICT (instance_id, repository, issue_number) DO NOTHING;
            """,
            connection,
            transaction))
        {
            AddCorrelationIdentityParameters(insert, correlation);
            insert.Parameters.AddWithValue("thread_id", correlation.ThreadId.Value);
            insert.Parameters.AddWithValue(
                "root_directive_id",
                correlation.RootDirectiveId.Value);
            insert.Parameters.AddWithValue("created_at", createdAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var verify = new NpgsqlCommand(
            $"""
            SELECT organization_id, thread_id, root_directive_id
            FROM {GitHubIssuesInboundSchema.SchemaName}.issue_correlations
            WHERE instance_id = @instance_id
              AND repository = @repository
              AND issue_number = @issue_number;
            """,
            connection,
            transaction);
        AddCorrelationIdentityParameters(verify, correlation);
        await using var reader = await verify
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(
                reader.GetString(0),
                correlation.OrganizationId.Value,
                StringComparison.Ordinal)
            || reader.GetGuid(1) != correlation.ThreadId.Value
            || reader.GetGuid(2) != correlation.RootDirectiveId.Value)
        {
            throw new InvalidOperationException(
                "The persisted GitHub issue correlation conflicts with the submitted directive.");
        }
    }

    private static async Task InsertOrVerifyDirectiveCorrelationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GitHubIssuesInboundEnvelope envelope,
        GitHubIssueCorrelation correlation,
        DirectiveId directiveId,
        DateTimeOffset correlatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {GitHubIssuesInboundSchema.SchemaName}.issue_directive_correlations (
                instance_id,
                repository,
                issue_number,
                external_event_id,
                directive_id,
                correlated_at)
            VALUES (
                @instance_id,
                @repository,
                @issue_number,
                @external_event_id,
                @directive_id,
                @correlated_at)
            ON CONFLICT DO NOTHING;
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("instance_id", correlation.InstanceId);
            insert.Parameters.AddWithValue("repository", correlation.Repository);
            insert.Parameters.AddWithValue("issue_number", correlation.IssueNumber);
            insert.Parameters.AddWithValue("external_event_id", envelope.ExternalEventId);
            insert.Parameters.AddWithValue("directive_id", directiveId.Value);
            insert.Parameters.AddWithValue("correlated_at", correlatedAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var verify = new NpgsqlCommand(
            $"""
            SELECT issue_number, directive_id
            FROM {GitHubIssuesInboundSchema.SchemaName}.issue_directive_correlations
            WHERE instance_id = @instance_id
              AND repository = @repository
              AND external_event_id = @external_event_id;
            """,
            connection,
            transaction);
        verify.Parameters.AddWithValue("instance_id", correlation.InstanceId);
        verify.Parameters.AddWithValue("repository", correlation.Repository);
        verify.Parameters.AddWithValue("external_event_id", envelope.ExternalEventId);
        await using var reader = await verify
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) != correlation.IssueNumber
            || reader.GetGuid(1) != directiveId.Value)
        {
            throw new InvalidOperationException(
                "The persisted GitHub directive correlation conflicts with the staged event.");
        }
    }

    private static async ValueTask<GitHubIssueCorrelation?> ReadCorrelationAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using (command)
        {
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new GitHubIssueCorrelation(
                reader.GetString(0),
                OrganizationId.From(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt64(3),
                ThreadId.From(reader.GetGuid(4)),
                DirectiveId.From(reader.GetGuid(5)));
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

    private static void ValidateCorrelationIdentity(
        string instanceId,
        OrganizationId organizationId,
        string repository,
        long issueNumber)
    {
        ValidateCorrelationScope(instanceId, organizationId);
        if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
        {
            throw new ArgumentException(
                "Repository must be a trimmed 'owner/repository' identifier.",
                nameof(repository));
        }

        if (issueNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(issueNumber),
                issueNumber,
                "GitHub issue number must be positive.");
        }
    }

    private static void ValidateCorrelationScope(
        string instanceId,
        OrganizationId organizationId)
    {
        GitHubIssuesConnectorInstanceConfiguration.RequireInstanceId(instanceId, nameof(instanceId));
        ArgumentNullException.ThrowIfNull(organizationId);
    }

    private static void AddIdentityParameters(
        NpgsqlCommand command,
        string instanceId,
        string repository)
    {
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("repository", repository.ToLowerInvariant());
    }

    private static void AddCorrelationIdentityParameters(
        NpgsqlCommand command,
        string instanceId,
        OrganizationId organizationId,
        string repository,
        long issueNumber)
    {
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("organization_id", organizationId.Value);
        command.Parameters.AddWithValue("repository", repository.ToLowerInvariant());
        command.Parameters.AddWithValue("issue_number", issueNumber);
    }

    private static void AddCorrelationIdentityParameters(
        NpgsqlCommand command,
        GitHubIssueCorrelation correlation) =>
        AddCorrelationIdentityParameters(
            command,
            correlation.InstanceId,
            correlation.OrganizationId,
            correlation.Repository,
            correlation.IssueNumber);
}
