using Npgsql;
using Testcontainers.PostgreSql;

namespace Hive.Tests.PostgreSql;

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL registry";
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AkkaPostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "Akka cluster with PostgreSQL";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create(ConnectionString);

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task ResetRegistryAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS registry CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    public async Task ResetPersistenceAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS persistence CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
