using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Evaluation;
using Hive.Infrastructure.Evaluation.PostgreSql;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlEvaluationProjectionTests(PostgreSqlFixture fixture)
{
    private static readonly OrganizationId Acme = OrganizationId.From("acme-delivery");
    private static readonly PositionId BugTriage = PositionId.From("bug-triage");
    private static readonly PositionId FollowUpCoordinator = PositionId.From("follow-up-coordinator");
    private static readonly DateTimeOffset SentAt =
        new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_and_projector_store_generic_header_and_dimension_lines_with_isolation()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        var migrator = new PostgreSqlEvaluationProjectionMigrator(dataSource);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        await using var projector = Projector(dataSource);
        var thread = ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002001"));
        var directive = DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002001"));

        await projector.ProjectAsync(
            directive,
            ReportMessage(
                Acme,
                BugTriage,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002001")),
                "Private report body.\n" +
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[\"run-log\",\"environment\"]}}"));
        await projector.ProjectAsync(
            directive,
            ReportMessage(
                OrganizationId.From("other-org"),
                BugTriage,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002002")),
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"critical\"],\"missing-information\":[]}}"));
        await projector.ProjectAsync(
            directive,
            ReportMessage(
                Acme,
                PositionId.From("other-position"),
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002003")),
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"critical\"],\"missing-information\":[]}}"));

        await using var header = dataSource.CreateCommand(
            """
            SELECT organization_id, position_id, contract_version, rubric_version
            FROM evaluation.result_projections;
            """);
        await using var headerReader = await header.ExecuteReaderAsync();
        Assert.True(await headerReader.ReadAsync());
        Assert.Equal("acme-delivery", headerReader.GetString(0));
        Assert.Equal("bug-triage", headerReader.GetString(1));
        Assert.Equal(EvaluationProjectionParser.ContractVersion, headerReader.GetInt32(2));
        Assert.Equal(1, headerReader.GetInt32(3));
        Assert.False(await headerReader.ReadAsync());
        await headerReader.CloseAsync();

        var dimensions = new List<(string Id, string Status, string[] Labels)>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT dimension_id, status, labels
            FROM evaluation.result_projection_dimensions
            WHERE organization_id = 'acme-delivery'
            ORDER BY dimension_id;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                dimensions.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<string[]>(2)));
            }
        }

        Assert.Equal(["decision", "missing-information", "severity"], dimensions.Select(item => item.Id));
        Assert.All(dimensions, item => Assert.Equal("valid", item.Status));
        Assert.Equal(["report"], dimensions[0].Labels);
        Assert.Equal(["environment", "run-log"], dimensions[1].Labels);
        Assert.Equal(["high"], dimensions[2].Labels);

        await using var jsonCommand = dataSource.CreateCommand(
            """
            SELECT jsonb_build_object(
                'headers', (SELECT jsonb_agg(to_jsonb(projected)) FROM evaluation.result_projections projected),
                'dimensions', (SELECT jsonb_agg(to_jsonb(dimension)) FROM evaluation.result_projection_dimensions dimension)
            )::text;
            """);
        var persistedJson = (string)(await jsonCommand.ExecuteScalarAsync())!;
        Assert.DoesNotContain("Private report body", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("other-org", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("other-position", persistedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projection_is_first_writer_wins_under_concurrency()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        await new PostgreSqlEvaluationProjectionMigrator(dataSource).MigrateAsync();
        await using var projector = Projector(dataSource);
        var thread = ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002011"));
        var directive = DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002011"));

        var first = projector.ProjectAsync(
            directive,
            ReportMessage(
                Acme,
                BugTriage,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002011")),
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"medium\"],\"missing-information\":[]}}"))
            .AsTask();
        var second = projector.ProjectAsync(
            directive,
            ReportMessage(
                Acme,
                BugTriage,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002012")),
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"critical\"],\"missing-information\":[\"run-log\"]}}"))
            .AsTask();
        await Task.WhenAll(first, second);

        var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var command = dataSource.CreateCommand(
            """
            SELECT dimension_id, labels
            FROM evaluation.result_projection_dimensions
            ORDER BY dimension_id;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0), reader.GetFieldValue<string[]>(1));
        }

        Assert.Equal(3, values.Count);
        Assert.True(
            (values["severity"].SequenceEqual(["medium"]) && values["missing-information"].Length == 0)
            || (values["severity"].SequenceEqual(["critical"]) && values["missing-information"].SequenceEqual(["run-log"])));
    }

    [Fact]
    public async Task Second_role_fixture_uses_the_same_storage_with_partial_failure_minimization()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        await new PostgreSqlEvaluationProjectionMigrator(dataSource).MigrateAsync();
        await using var projector = new PostgreSqlEvaluationResultProjector(
            dataSource,
            Acme,
            FollowUpCoordinator,
            EvaluationRubricContract.Load(FollowUpRubricFile, 1));
        var thread = ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002031"));
        var directive = DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002031"));

        await projector.ProjectAsync(
            directive,
            ReportMessage(
                Acme,
                FollowUpCoordinator,
                thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002031")),
                "Private coordination narrative.\n" +
                "hive-evaluation-v1:{\"dimensions\":{\"response-window\":[\"same-day\"],\"pending-signals\":[\"attendee_reply\",\"private-rejected-value\"]}}"));

        var rows = new Dictionary<string, (string Status, string[] Labels)>(StringComparer.Ordinal);
        await using var command = dataSource.CreateCommand(
            "SELECT dimension_id, status, labels FROM evaluation.result_projection_dimensions ORDER BY dimension_id;");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                reader.GetString(0),
                (reader.GetString(1), reader.GetFieldValue<string[]>(2)));
        }

        Assert.Equal(
            ["coordination-route", "pending-signals", "response-window"],
            rows.Keys);
        Assert.Equal("valid", rows["coordination-route"].Status);
        Assert.Equal(["track"], rows["coordination-route"].Labels);
        Assert.Equal("invalid", rows["pending-signals"].Status);
        Assert.Empty(rows["pending-signals"].Labels);
        Assert.Equal("valid", rows["response-window"].Status);
        Assert.Equal(["same-day"], rows["response-window"].Labels);

        await reader.CloseAsync();
        await using var persisted = dataSource.CreateCommand(
            "SELECT jsonb_agg(to_jsonb(row))::text FROM evaluation.result_projection_dimensions AS row;");
        var persistedJson = (string)(await persisted.ExecuteScalarAsync())!;
        Assert.DoesNotContain("Private coordination narrative", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-rejected-value", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("severity", persistedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing-information", persistedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_dimension_is_redacted_without_discarding_valid_dimensions()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        await new PostgreSqlEvaluationProjectionMigrator(dataSource).MigrateAsync();
        await using var projector = Projector(dataSource);

        await projector.ProjectAsync(
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000002021")),
            ReportMessage(
                Acme,
                BugTriage,
                ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000002021")),
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000002021")),
                "hive-evaluation-v1:{\"dimensions\":{\"severity\":[\"high\"],\"missing-information\":[\"environment\",\"rejected-private-value\"]}}"));

        var rows = new Dictionary<string, (string Status, string[] Labels)>(StringComparer.Ordinal);
        await using var command = dataSource.CreateCommand(
            "SELECT dimension_id, status, labels FROM evaluation.result_projection_dimensions;");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                reader.GetString(0),
                (reader.GetString(1), reader.GetFieldValue<string[]>(2)));
        }

        Assert.Equal("valid", rows["severity"].Status);
        Assert.Equal(["high"], rows["severity"].Labels);
        Assert.Equal("invalid", rows["missing-information"].Status);
        Assert.Empty(rows["missing-information"].Labels);
        Assert.DoesNotContain(
            rows.SelectMany(row => row.Value.Labels),
            label => label == "rejected-private-value");
    }

    [Fact]
    public async Task Legacy_schema_is_replaced_idempotently_by_the_current_migration()
    {
        await using var dataSource = fixture.CreateDataSource();
        await ResetAsync(dataSource);
        await using (var legacy = dataSource.CreateCommand(
            """
            CREATE SCHEMA evaluation;
            CREATE TABLE evaluation.schema_migrations (
                version integer PRIMARY KEY,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO evaluation.schema_migrations (version, name) VALUES (1, 'legacy');
            CREATE TABLE evaluation.result_projections (
                organization_id text NOT NULL,
                legacy_payload text NULL
            );
            """))
        {
            await legacy.ExecuteNonQueryAsync();
        }

        var migrator = new PostgreSqlEvaluationProjectionMigrator(dataSource);
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var columns = dataSource.CreateCommand(
            """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'evaluation'
              AND table_name IN ('result_projections', 'result_projection_dimensions')
            ORDER BY table_name, ordinal_position;
            """);
        await using var columnReader = await columns.ExecuteReaderAsync();
        var schema = new List<(string Table, string Column)>();
        while (await columnReader.ReadAsync())
        {
            schema.Add((columnReader.GetString(0), columnReader.GetString(1)));
        }

        Assert.DoesNotContain(schema, item => item.Column == "legacy_payload");
        Assert.Contains(schema, item => item == ("result_projections", "contract_version"));
        Assert.Contains(schema, item => item == ("result_projection_dimensions", "dimension_id"));
        Assert.Contains(schema, item => item == ("result_projection_dimensions", "diagnostic_code"));
    }

    private static PostgreSqlEvaluationResultProjector Projector(NpgsqlDataSource dataSource) =>
        new(dataSource, Acme, BugTriage, EvaluationRubricContract.Load(RubricFile, 1));

    private static Report ReportMessage(
        OrganizationId organization,
        PositionId sourcePosition,
        ThreadId thread,
        MessageId message,
        string body) =>
        new(
            message,
            organization,
            new PositionEndpointRef(sourcePosition),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            thread,
            Priority.Normal,
            1,
            SentAt,
            deadline: null,
            DirectiveId.From(Guid.Parse("dddddddd-0000-0000-0000-000000002001")),
            ReportKind.Done,
            body);

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

    private static string FollowUpRubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "follow-up-coordination-rubric.v1.json");

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
