using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOrganizationRegistryMigrationTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Migration_is_versioned_and_idempotent()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlOrganizationRegistryMigrator(dataSource);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        var tableNames = new List<string>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'registry'
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
            "SELECT version FROM registry.schema_migrations ORDER BY version;"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        Assert.Equal(
            [
                "authorities",
                "command_relations",
                "occupants",
                "organization_import_locks",
                "organizations",
                "positions",
                "schedules",
                "schema_migrations",
                "units",
            ],
            tableNames);
        Assert.Equal([1], appliedVersions);
    }
}
