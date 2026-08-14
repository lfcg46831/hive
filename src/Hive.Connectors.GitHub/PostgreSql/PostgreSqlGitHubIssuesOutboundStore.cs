using Npgsql;
using NpgsqlTypes;

namespace Hive.Connectors.GitHub.PostgreSql;

internal sealed class PostgreSqlGitHubIssuesOutboundStore
    : IGitHubIssuesOutboundStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlGitHubIssuesOutboundStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _ownsDataSource = true;
    }

    internal PostgreSqlGitHubIssuesOutboundStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<IGitHubIssuesOutboundOperationLease> AcquireAsync(
        GitHubIssuesOutboundOperationDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtext(@lock_key));",
                connection))
            {
                lockCommand.Parameters.AddWithValue(
                    "lock_key",
                    $"hive.github-outbound:{descriptor.OperationKey}");
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertAsync(connection, descriptor, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadAndVerifyAsync(connection, descriptor, cancellationToken)
                .ConfigureAwait(false);
            return new Lease(connection, descriptor.OperationKey, snapshot);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection,
        GitHubIssuesOutboundOperationDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {GitHubIssuesInboundSchema.SchemaName}.outbound_operations (
                operation_key, payload_hash, instance_id, organization_id, repository,
                issue_number, thread_id, directive_id, position_id, tool_name,
                operation_state, attempt_count, created_at, updated_at)
            VALUES (
                @operation_key, @payload_hash, @instance_id, @organization_id, @repository,
                @issue_number, @thread_id, @directive_id, @position_id, @tool_name,
                'pending', 0, @created_at, @created_at)
            ON CONFLICT (operation_key) DO NOTHING;
            """,
            connection);
        AddDescriptorParameters(command, descriptor);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubIssuesOutboundOperationSnapshot> ReadAndVerifyAsync(
        NpgsqlConnection connection,
        GitHubIssuesOutboundOperationDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT payload_hash, instance_id, organization_id, repository, issue_number,
                   thread_id, directive_id, position_id, tool_name, operation_state,
                   attempt_count, last_code, receipt
            FROM {GitHubIssuesInboundSchema.SchemaName}.outbound_operations
            WHERE operation_key = @operation_key;
            """,
            connection);
        command.Parameters.AddWithValue("operation_key", descriptor.OperationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !string.Equals(reader.GetString(0), descriptor.PayloadHash, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), descriptor.Issue.InstanceId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), descriptor.Issue.OrganizationId.Value, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), descriptor.Issue.Repository, StringComparison.Ordinal)
            || reader.GetInt64(4) != descriptor.Issue.IssueNumber
            || reader.GetGuid(5) != descriptor.Issue.ThreadId.Value
            || reader.GetGuid(6) != descriptor.DirectiveId.Value
            || !string.Equals(reader.GetString(7), descriptor.PositionId.Value, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(8), descriptor.ToolName, StringComparison.Ordinal))
        {
            throw new GitHubIssuesOutboundOperationConflictException();
        }

        var state = reader.GetString(9) switch
        {
            "pending" => GitHubIssuesOutboundOperationState.Pending,
            "succeeded" => GitHubIssuesOutboundOperationState.Succeeded,
            "rejected" => GitHubIssuesOutboundOperationState.Rejected,
            _ => throw new InvalidOperationException("Persisted GitHub outbound operation state is invalid."),
        };
        return new GitHubIssuesOutboundOperationSnapshot(
            state,
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private static void AddDescriptorParameters(
        NpgsqlCommand command,
        GitHubIssuesOutboundOperationDescriptor descriptor)
    {
        command.Parameters.AddWithValue("operation_key", descriptor.OperationKey);
        command.Parameters.AddWithValue("payload_hash", descriptor.PayloadHash);
        command.Parameters.AddWithValue("instance_id", descriptor.Issue.InstanceId);
        command.Parameters.AddWithValue("organization_id", descriptor.Issue.OrganizationId.Value);
        command.Parameters.AddWithValue("repository", descriptor.Issue.Repository);
        command.Parameters.AddWithValue("issue_number", descriptor.Issue.IssueNumber);
        command.Parameters.AddWithValue("thread_id", descriptor.Issue.ThreadId.Value);
        command.Parameters.AddWithValue("directive_id", descriptor.DirectiveId.Value);
        command.Parameters.AddWithValue("position_id", descriptor.PositionId.Value);
        command.Parameters.AddWithValue("tool_name", descriptor.ToolName);
        command.Parameters.AddWithValue("created_at", descriptor.CreatedAtUtc);
    }

    private sealed class Lease : IGitHubIssuesOutboundOperationLease
    {
        private readonly NpgsqlConnection _connection;
        private readonly string _operationKey;
        private bool _disposed;

        public Lease(
            NpgsqlConnection connection,
            string operationKey,
            GitHubIssuesOutboundOperationSnapshot snapshot)
        {
            _connection = connection;
            _operationKey = operationKey;
            Snapshot = snapshot;
        }

        public GitHubIssuesOutboundOperationSnapshot Snapshot { get; private set; }

        public async Task RecordAttemptAsync(
            string code,
            DateTimeOffset attemptedAtUtc,
            CancellationToken cancellationToken = default)
        {
            EnsurePending();
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            await using var command = new NpgsqlCommand(
                $"""
                UPDATE {GitHubIssuesInboundSchema.SchemaName}.outbound_operations
                SET attempt_count = attempt_count + 1,
                    last_code = @last_code,
                    updated_at = @updated_at
                WHERE operation_key = @operation_key AND operation_state = 'pending';
                """,
                _connection);
            command.Parameters.AddWithValue("operation_key", _operationKey);
            command.Parameters.AddWithValue("last_code", code);
            command.Parameters.AddWithValue("updated_at", attemptedAtUtc);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("GitHub outbound operation changed during an attempt.");
            }

            Snapshot = Snapshot with
            {
                AttemptCount = Snapshot.AttemptCount + 1,
                LastCode = code,
            };
        }

        public async Task CompleteSuccessAsync(
            string receipt,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            EnsurePending();
            ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
            await CompleteAsync("succeeded", lastCode: null, receipt, completedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            Snapshot = new(
                GitHubIssuesOutboundOperationState.Succeeded,
                Snapshot.AttemptCount,
                LastCode: null,
                receipt);
        }

        public async Task CompleteRejectedAsync(
            string errorCode,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            EnsurePending();
            ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
            await CompleteAsync("rejected", errorCode, receipt: null, completedAtUtc, cancellationToken)
                .ConfigureAwait(false);
            Snapshot = new(
                GitHubIssuesOutboundOperationState.Rejected,
                Snapshot.AttemptCount,
                errorCode,
                Receipt: null);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await using var unlock = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(hashtext(@lock_key));",
                    _connection);
                unlock.Parameters.AddWithValue(
                    "lock_key",
                    $"hive.github-outbound:{_operationKey}");
                await unlock.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task CompleteAsync(
            string state,
            string? lastCode,
            string? receipt,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(
                $"""
                UPDATE {GitHubIssuesInboundSchema.SchemaName}.outbound_operations
                SET operation_state = @operation_state,
                    last_code = @last_code,
                    receipt = @receipt,
                    updated_at = @completed_at,
                    completed_at = @completed_at
                WHERE operation_key = @operation_key AND operation_state = 'pending';
                """,
                _connection);
            command.Parameters.AddWithValue("operation_key", _operationKey);
            command.Parameters.AddWithValue("operation_state", state);
            command.Parameters.Add("last_code", NpgsqlDbType.Text).Value =
                lastCode is null ? DBNull.Value : lastCode;
            command.Parameters.Add("receipt", NpgsqlDbType.Text).Value =
                receipt is null ? DBNull.Value : receipt;
            command.Parameters.AddWithValue("completed_at", completedAtUtc);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("GitHub outbound operation changed during completion.");
            }
        }

        private void EnsurePending()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Snapshot.State != GitHubIssuesOutboundOperationState.Pending)
            {
                throw new InvalidOperationException("Only pending GitHub outbound operations can change.");
            }
        }
    }
}
