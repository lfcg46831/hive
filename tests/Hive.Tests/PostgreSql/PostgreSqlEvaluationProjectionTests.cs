using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Evaluation;
using Hive.Infrastructure.Evaluation.PostgreSql;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlEvaluationProjectionTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset SentAt =
        new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_and_projector_store_only_safe_labels_with_organization_isolation()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        var migrator = new PostgreSqlEvaluationProjectionMigrator(dataSource);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        await using var projector = new PostgreSqlEvaluationResultProjector(
            dataSource,
            BugTriageEvaluationVocabulary.Load(RubricFile));
        var thread = ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002001"));
        var directive = DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002001"));

        await projector.ProjectAsync(
            directive,
            ReportMessage(
                OrganizationId.From("acme-delivery"),
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002001")),
                "Private report body.\n" +
                "hive-evaluation-v1:{\"severity\":\"high\",\"missing_information\":[\"run-log\",\"environment\"]}"));
        await projector.ProjectAsync(
            directive,
            EscalationMessage(
                OrganizationId.From("other-org"),
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002002")),
                "Private escalation context.\n" +
                "hive-evaluation-v1:{\"severity\":\"critical\",\"missing_information\":[]}"));

        var rows = new List<(string Organization, string Severity, string[] Missing)>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT organization_id, severity, missing_information
            FROM evaluation.result_projections
            ORDER BY organization_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2)));
            }
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(("acme-delivery", "high"), (rows[0].Organization, rows[0].Severity));
        Assert.Equal(["environment", "run-log"], rows[0].Missing);
        Assert.Equal(("other-org", "critical"), (rows[1].Organization, rows[1].Severity));
        Assert.Empty(rows[1].Missing);

        var columns = new List<string>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'evaluation'
              AND table_name = 'result_projections'
            ORDER BY ordinal_position;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        }

        Assert.Equal(
        [
            "organization_id",
            "thread_id",
            "directive_id",
            "message_id",
            "projection_version",
            "severity",
            "missing_information",
        ], columns);

        await using var jsonCommand = dataSource.CreateCommand(
            "SELECT jsonb_agg(to_jsonb(projected))::text FROM evaluation.result_projections projected;");
        var persistedJson = (string)(await jsonCommand.ExecuteScalarAsync())!;
        Assert.DoesNotContain("Private report body", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Private escalation context", persistedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_is_first_writer_idempotent_for_stable_correlation()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        await new PostgreSqlEvaluationProjectionMigrator(dataSource).MigrateAsync();
        await using var projector = new PostgreSqlEvaluationResultProjector(
            dataSource,
            BugTriageEvaluationVocabulary.Load(RubricFile));
        var organization = OrganizationId.From("acme-delivery");
        var thread = ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002011"));
        var directive = DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002011"));

        await projector.ProjectAsync(
            directive,
            ReportMessage(
                organization,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002011")),
                "hive-evaluation-v1:{\"severity\":\"medium\",\"missing_information\":[]}"));
        await projector.ProjectAsync(
            directive,
            ReportMessage(
                organization,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002012")),
                "hive-evaluation-v1:{\"severity\":\"critical\",\"missing_information\":[\"run-log\"]}"));

        await using var command = dataSource.CreateCommand(
            "SELECT severity, missing_information FROM evaluation.result_projections;");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("medium", reader.GetString(0));
        Assert.Empty(reader.GetFieldValue<string[]>(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Projection_preserves_severity_and_nulls_an_invalid_missing_information_list()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        await new PostgreSqlEvaluationProjectionMigrator(dataSource).MigrateAsync();
        await using var projector = new PostgreSqlEvaluationResultProjector(
            dataSource,
            BugTriageEvaluationVocabulary.Load(RubricFile));

        await projector.ProjectAsync(
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002021")),
            ReportMessage(
                OrganizationId.From("acme-delivery"),
                ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002021")),
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002021")),
                "hive-evaluation-v1:{\"severity\":\"high\",\"missing_information\":[\"environment\",\"correlation_metadata\"]}"));

        await using var command = dataSource.CreateCommand(
            "SELECT severity, missing_information FROM evaluation.result_projections;");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("high", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }

    private static Report ReportMessage(
        OrganizationId organization,
        ThreadId thread,
        MessageId message,
        string body) =>
        new(
            message,
            organization,
            new PositionEndpointRef(PositionId.From("bug-triage")),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            thread,
            Priority.Normal,
            1,
            SentAt,
            deadline: null,
            DirectiveId.From(Guid.Parse("dddddddd-0000-0000-0000-000000002001")),
            ReportKind.Done,
            body);

    private static Escalation EscalationMessage(
        OrganizationId organization,
        ThreadId thread,
        MessageId message,
        string context) =>
        new(
            message,
            organization,
            new PositionEndpointRef(PositionId.From("bug-triage")),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            thread,
            Priority.High,
            1,
            SentAt,
            deadline: null,
            "Needs review",
            context,
            []);

    private static async Task ResetAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS evaluation CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static string RubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }
}
