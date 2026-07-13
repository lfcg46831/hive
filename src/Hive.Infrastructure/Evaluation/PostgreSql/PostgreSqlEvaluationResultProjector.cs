using Hive.Domain.Evaluation;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace Hive.Infrastructure.Evaluation.PostgreSql;

public sealed class PostgreSqlEvaluationResultProjector : IEvaluationResultProjector, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly BugTriageEvaluationVocabulary _vocabulary;
    private readonly bool _ownsDataSource;

    public PostgreSqlEvaluationResultProjector(
        string connectionString,
        BugTriageEvaluationVocabulary vocabulary)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "PostgreSQL connection string is required for evaluation projection.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        _ownsDataSource = true;
    }

    public PostgreSqlEvaluationResultProjector(
        NpgsqlDataSource dataSource,
        BugTriageEvaluationVocabulary vocabulary)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
    }

    public async ValueTask ProjectAsync(
        DirectiveId directiveId,
        OrgMessage resultMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directiveId);
        ArgumentNullException.ThrowIfNull(resultMessage);

        var content = resultMessage switch
        {
            Report report => report.Body,
            Escalation escalation => escalation.Context,
            _ => null,
        };
        if (content is null
            || BugTriageEvaluationLabelParser.Parse(content, _vocabulary) is not { } labels)
        {
            return;
        }

        await using var command = _dataSource.CreateCommand(
            """
            INSERT INTO evaluation.result_projections (
                organization_id,
                thread_id,
                directive_id,
                message_id,
                projection_version,
                severity,
                missing_information)
            VALUES (
                @organization_id,
                @thread_id,
                @directive_id,
                @message_id,
                @projection_version,
                @severity,
                @missing_information)
            ON CONFLICT (organization_id, thread_id, directive_id) DO NOTHING;
            """);
        command.Parameters.Add("organization_id", NpgsqlDbType.Text).Value =
            resultMessage.OrganizationId.Value;
        command.Parameters.Add("thread_id", NpgsqlDbType.Uuid).Value =
            resultMessage.Thread.Value;
        command.Parameters.Add("directive_id", NpgsqlDbType.Uuid).Value =
            directiveId.Value;
        command.Parameters.Add("message_id", NpgsqlDbType.Uuid).Value =
            resultMessage.Id.Value;
        command.Parameters.Add("projection_version", NpgsqlDbType.Integer).Value =
            BugTriageEvaluationLabelParser.ProjectionVersion;
        command.Parameters.Add("severity", NpgsqlDbType.Text).Value =
            labels.Severity is null ? DBNull.Value : labels.Severity;
        command.Parameters.Add("missing_information", NpgsqlDbType.Array | NpgsqlDbType.Text).Value =
            labels.MissingInformation is null
                ? DBNull.Value
                : labels.MissingInformation.ToArray();

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() =>
        _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;
}
