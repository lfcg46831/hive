using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Persistence.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionPersistenceMigrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Migration_creates_dedicated_schema_and_is_idempotent()
    {
        await fixture.ResetPersistenceAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlPositionPersistenceMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        // The migration owns only the dedicated schema and its ledger; the journal/snapshot tables
        // are created by the Akka.Persistence.Sql plugin's auto-initialization on first use, so they
        // do not exist yet at migration time.
        var tableNames = new List<string>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'persistence'
            ORDER BY table_name;
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        var appliedVersions = new List<int>();
        await using (var command = dataSource.CreateCommand(
            "SELECT version FROM persistence.schema_migrations ORDER BY version;"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal(["schema_migrations"], tableNames);
        Assert.Equal([1], appliedVersions);
    }

    [Fact]
    public async Task Hive_bootstrap_creates_persistence_schema_before_host_start_completes()
    {
        await fixture.ResetPersistenceAsync();
        await using var dataSource = fixture.CreateDataSource();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "agents",
            ["Hive:Organizations:RootPath"] = Path.Combine(
                RepositoryRoot,
                "config",
                "organizations"),
            ["ConnectionStrings:PostgreSql"] = fixture.ConnectionString,
        });
        builder.AddHiveBootstrap();
        using var host = builder.Build();

        await host.StartAsync();

        await using var command = dataSource.CreateCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM information_schema.schemata
                    WHERE schema_name = 'persistence'),
                to_regclass('persistence.schema_migrations') IS NOT NULL;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));

        await host.StopAsync();
    }

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
