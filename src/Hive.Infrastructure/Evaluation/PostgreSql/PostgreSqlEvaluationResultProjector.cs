using Hive.Domain.Evaluation;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Evaluation.PostgreSql;

public sealed class PostgreSqlEvaluationResultProjector : IEvaluationResultProjector, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly EvaluationProfileCatalog _catalog;
    private readonly bool _ownsDataSource;

    internal PostgreSqlEvaluationResultProjector(
        string connectionString,
        OrganizationId organizationId,
        PositionId positionId,
        EvaluationRubricContract rubric)
        : this(
            CreateDataSource(connectionString),
            EvaluationProfileCatalog.Single(organizationId, positionId, rubric),
            ownsDataSource: true)
    {
    }

    internal PostgreSqlEvaluationResultProjector(
        NpgsqlDataSource dataSource,
        OrganizationId organizationId,
        PositionId positionId,
        EvaluationRubricContract rubric)
        : this(
            dataSource,
            EvaluationProfileCatalog.Single(organizationId, positionId, rubric),
            ownsDataSource: false)
    {
    }

    internal PostgreSqlEvaluationResultProjector(
        string connectionString,
        EvaluationProfileCatalog catalog)
        : this(CreateDataSource(connectionString), catalog, ownsDataSource: true)
    {
    }

    private PostgreSqlEvaluationResultProjector(
        NpgsqlDataSource dataSource,
        EvaluationProfileCatalog catalog,
        bool ownsDataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ownsDataSource = ownsDataSource;
    }

    public async ValueTask ProjectAsync(
        DirectiveId directiveId,
        OrgMessage resultMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directiveId);
        ArgumentNullException.ThrowIfNull(resultMessage);

        if (resultMessage.From is not PositionEndpointRef sourcePosition
            || _catalog.Resolve(resultMessage.OrganizationId, sourcePosition.PositionId)
                is not { } profile)
        {
            return;
        }

        var (messageKind, content) = ReadResultFacts(resultMessage);
        var projection = EvaluationProjectionParser.Parse(
            content,
            messageKind,
            profile.Rubric);

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var header = new NpgsqlCommand(
            """
            INSERT INTO evaluation.result_projections (
                organization_id,
                position_id,
                thread_id,
                directive_id,
                message_id,
                contract_version,
                rubric_version)
            VALUES (
                @organization_id,
                @position_id,
                @thread_id,
                @directive_id,
                @message_id,
                @contract_version,
                @rubric_version)
            ON CONFLICT DO NOTHING
            RETURNING 1;
            """,
            connection,
            transaction);
        header.Parameters.Add("organization_id", NpgsqlDbType.Text).Value =
            resultMessage.OrganizationId.Value;
        header.Parameters.Add("position_id", NpgsqlDbType.Text).Value =
            sourcePosition.PositionId.Value;
        header.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value =
            resultMessage.Thread.Value;
        header.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value =
            directiveId.Value;
        header.Parameters.Add("message_id", NpgsqlDbType.Uuid).Value =
            resultMessage.Id.Value;
        header.Parameters.Add("contract_version", NpgsqlDbType.Integer).Value =
            projection.ContractVersion;
        header.Parameters.Add("rubric_version", NpgsqlDbType.Integer).Value =
            projection.RubricVersion;

        var inserted = await header.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (inserted is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var dimension in projection.Dimensions)
        {
            await using var line = new NpgsqlCommand(
                """
                INSERT INTO evaluation.result_projection_dimensions (
                    organization_id,
                    thread_id,
                    directive_id,
                    dimension_id,
                    status,
                    labels)
                VALUES (
                    @organization_id,
                    @thread_id,
                    @directive_id,
                    @dimension_id,
                    @status,
                    @labels);
                """,
                connection,
                transaction);
            line.Parameters.Add("organization_id", NpgsqlDbType.Text).Value =
                resultMessage.OrganizationId.Value;
            line.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value =
                resultMessage.Thread.Value;
            line.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value =
                directiveId.Value;
            line.Parameters.Add("dimension_id", NpgsqlDbType.Text).Value =
                dimension.DimensionId;
            line.Parameters.Add("status", NpgsqlDbType.Text).Value =
                StatusValue(dimension.Status);
            line.Parameters.Add("labels", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
                dimension.Labels.ToArray();
            await line.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() =>
        _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    private static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "PostgreSQL connection string is required for evaluation projection.",
                nameof(connectionString));
        }

        return NpgsqlDataSource.Create(connectionString);
    }

    private static (string Kind, string? Content) ReadResultFacts(OrgMessage message) =>
        message switch
        {
            Report report => ("report", report.Body),
            Escalation escalation => ("escalation", escalation.Context),
            _ => (message.GetType().Name, null),
        };

    private static string StatusValue(EvaluationDimensionStatus status) => status switch
    {
        EvaluationDimensionStatus.Valid => "valid",
        EvaluationDimensionStatus.Missing => "missing",
        EvaluationDimensionStatus.Invalid => "invalid",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
