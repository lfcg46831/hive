using Npgsql;

namespace Hive.Infrastructure.OccupantChannels.PostgreSql;

internal sealed class PostgreSqlOccupantChannelDecisionTokenUseStore
    : IOccupantChannelDecisionTokenUseStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgreSqlOccupantChannelDecisionTokenUseStore(string connectionString)
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

    internal PostgreSqlOccupantChannelDecisionTokenUseStore(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<OccupantChannelDecisionTokenUseResult> TryConsumeAsync(
        Guid tokenId,
        Guid operationId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (tokenId == Guid.Empty)
        {
            throw new ArgumentException("Decision token id cannot be empty.", nameof(tokenId));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Decision token operation id cannot be empty.", nameof(operationId));
        }

        RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        RequireUtc(consumedAtUtc, nameof(consumedAtUtc));
        if (expiresAtUtc <= consumedAtUtc)
        {
            return OccupantChannelDecisionTokenUseResult.AlreadyConsumed;
        }

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var cleanup = new NpgsqlCommand(
            $"DELETE FROM {OccupantChannelTokenSchema.SchemaName}.decision_token_uses WHERE expires_at <= @consumed_at;",
            connection,
            transaction))
        {
            cleanup.Parameters.AddWithValue("consumed_at", consumedAtUtc);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int inserted;
        await using (var consume = new NpgsqlCommand(
            $"""
            INSERT INTO {OccupantChannelTokenSchema.SchemaName}.decision_token_uses (
                token_id,
                operation_id,
                expires_at,
                consumed_at)
            VALUES (
                @token_id,
                @operation_id,
                @expires_at,
                @consumed_at)
            ON CONFLICT (token_id) DO NOTHING;
            """,
            connection,
            transaction))
        {
            consume.Parameters.AddWithValue("token_id", tokenId);
            consume.Parameters.AddWithValue("operation_id", operationId);
            consume.Parameters.AddWithValue("expires_at", expiresAtUtc);
            consume.Parameters.AddWithValue("consumed_at", consumedAtUtc);
            inserted = await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (inserted == 1)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OccupantChannelDecisionTokenUseResult.Consumed;
        }

        Guid existingOperationId;
        await using (var read = new NpgsqlCommand(
            $"SELECT operation_id FROM {OccupantChannelTokenSchema.SchemaName}.decision_token_uses WHERE token_id = @token_id;",
            connection,
            transaction))
        {
            read.Parameters.AddWithValue("token_id", tokenId);
            existingOperationId = (Guid)(await read
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException(
                    "The consumed decision token could not be read back."));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return existingOperationId == operationId
            ? OccupantChannelDecisionTokenUseResult.AlreadyConsumedByOperation
            : OccupantChannelDecisionTokenUseResult.AlreadyConsumed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Decision token use timestamps must use the UTC offset.",
                parameterName);
        }
    }
}
