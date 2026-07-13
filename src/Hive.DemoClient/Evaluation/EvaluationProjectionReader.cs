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
        "Evaluation projection storage is not ready at schema version 2. Start HIVE with " +
        "docker-compose.evaluation.yml, recreate any stale evaluation profile host, and wait " +
        "for readiness before running evaluate.";

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
        try
        {
            await using var tables = _dataSource.CreateCommand(
                """
                SELECT
                    to_regclass('evaluation.schema_migrations') IS NOT NULL,
                    to_regclass('evaluation.result_projections') IS NOT NULL,
                    to_regclass('evaluation.result_projection_dimensions') IS NOT NULL;
                """);
            await using var tableReader = await tables.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var hasTableRow = await tableReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var migrationTableAvailable = hasTableRow && tableReader.GetBoolean(0);
            var headerTableAvailable = hasTableRow && tableReader.GetBoolean(1);
            var dimensionTableAvailable = hasTableRow && tableReader.GetBoolean(2);

            await tableReader.CloseAsync().ConfigureAwait(false);
            var currentVersionAvailable = false;
            if (migrationTableAvailable)
            {
                await using var version = _dataSource.CreateCommand(
                    "SELECT EXISTS (SELECT 1 FROM evaluation.schema_migrations WHERE version = 2);");
                currentVersionAvailable = await version.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) is true;
            }

            RequireAvailable(
                migrationTableAvailable,
                headerTableAvailable,
                dimensionTableAvailable,
                currentVersionAvailable);
        }
        catch (PostgresException exception)
            when (exception.SqlState is PostgresErrorCodes.UndefinedTable
                or PostgresErrorCodes.InvalidSchemaName)
        {
            throw new InvalidDataException(UnavailableMessage, exception);
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
                SELECT
                    projection.contract_version,
                    projection.rubric_version,
                    dimension.dimension_id,
                    dimension.status,
                    dimension.labels
                FROM evaluation.result_projections AS projection
                LEFT JOIN evaluation.result_projection_dimensions AS dimension
                  ON dimension.organization_id = projection.organization_id
                 AND dimension.thread_id = projection.thread_id
                 AND dimension.directive_id = projection.directive_id
                WHERE projection.organization_id = @organization_id
                  AND projection.thread_id = @thread_id
                  AND projection.directive_id = @directive_id
                ORDER BY dimension.dimension_id;
                """);
            command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value = organizationId;
            command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value = threadId;
            command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value = directiveId;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            int? contractVersion = null;
            int? rubricVersion = null;
            var dimensions = new List<EvaluationDimensionPrediction>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                contractVersion ??= reader.GetInt32(0);
                rubricVersion ??= reader.GetInt32(1);
                if (!reader.IsDBNull(2))
                {
                    dimensions.Add(new EvaluationDimensionPrediction(
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetFieldValue<string[]>(4)));
                }
            }

            if (contractVersion is null || rubricVersion is null)
            {
                return null;
            }

            return new EvaluationPrediction(
                contractVersion.Value,
                rubricVersion.Value,
                dimensions);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidDataException(UnavailableMessage, exception);
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    internal static void RequireAvailable(
        bool migrationTableAvailable,
        bool headerTableAvailable,
        bool dimensionTableAvailable,
        bool currentVersionAvailable)
    {
        if (!migrationTableAvailable
            || !headerTableAvailable
            || !dimensionTableAvailable
            || !currentVersionAvailable)
        {
            throw new InvalidDataException(UnavailableMessage);
        }
    }
}
