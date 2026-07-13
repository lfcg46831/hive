using Npgsql;
using NpgsqlTypes;

namespace Hive.DemoClient.Evaluation;

public interface IEvaluationProjectionReader : IAsyncDisposable
{
    Task<EvaluationPrediction?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken);
}

public sealed class NoopEvaluationProjectionReader : IEvaluationProjectionReader
{
    public static NoopEvaluationProjectionReader Instance { get; } = new();

    private NoopEvaluationProjectionReader()
    {
    }

    public Task<EvaluationPrediction?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<EvaluationPrediction?>(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class PostgreSqlEvaluationProjectionReader : IEvaluationProjectionReader
{
    public const string UnavailableMessage =
        "Evaluation projection storage is unavailable. Start HIVE with " +
        "docker-compose.evaluation.yml and wait for readiness before running evaluate.";

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlEvaluationProjectionReader(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT to_regclass('evaluation.result_projections') IS NOT NULL;");
        var available = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (available is not true)
        {
            throw new InvalidDataException(UnavailableMessage);
        }
    }

    public async Task<EvaluationPrediction?> ReadAsync(
        string organizationId,
        Guid threadId,
        Guid directiveId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _dataSource.CreateCommand(
                """
                SELECT projection_version, severity, missing_information
                FROM evaluation.result_projections
                WHERE organization_id = @organization_id
                  AND thread_id = @thread_id
                  AND directive_id = @directive_id;
                """);
            command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId;
            command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = threadId;
            command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value = directiveId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new EvaluationPrediction(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<string[]>(2));
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidDataException(UnavailableMessage, exception);
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
