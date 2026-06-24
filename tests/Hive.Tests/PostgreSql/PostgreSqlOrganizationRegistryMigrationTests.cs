using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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

    [Fact]
    public async Task Hive_bootstrap_migrates_and_imports_organizations_before_host_start_completes()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
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
            SELECT o.organization_id,
                   o.configuration_version,
                   (SELECT count(*) FROM registry.units u WHERE u.organization_id = o.organization_id),
                   (SELECT count(*) FROM registry.positions p WHERE p.organization_id = o.organization_id),
                   (SELECT count(*) FROM registry.schedules s WHERE s.organization_id = o.organization_id)
            FROM registry.organizations o;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("acme-delivery", reader.GetString(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(2, reader.GetInt64(2));
        Assert.Equal(2, reader.GetInt64(3));
        Assert.Equal(1, reader.GetInt64(4));
        Assert.False(await reader.ReadAsync());

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
