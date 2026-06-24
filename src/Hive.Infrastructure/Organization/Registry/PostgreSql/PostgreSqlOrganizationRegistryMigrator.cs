using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

public sealed partial class PostgreSqlOrganizationRegistryMigrator
{
    private const string ResourceMarker = ".Organization.Registry.PostgreSql.Migrations.";
    private readonly NpgsqlDataSource _dataSource;
    private readonly Assembly _assembly;

    public PostgreSqlOrganizationRegistryMigrator(NpgsqlDataSource dataSource)
        : this(dataSource, typeof(PostgreSqlOrganizationRegistryMigrator).Assembly)
    {
    }

    internal PostgreSqlOrganizationRegistryMigrator(
        NpgsqlDataSource dataSource,
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(assembly);

        _dataSource = dataSource;
        _assembly = assembly;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var migrations = DiscoverMigrations();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var bootstrap = new NpgsqlCommand(
            """
            SELECT pg_advisory_xact_lock(hashtext('hive.registry.migrations'));
            CREATE SCHEMA IF NOT EXISTS registry;
            CREATE TABLE IF NOT EXISTS registry.schema_migrations (
                version integer PRIMARY KEY,
                name text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """,
            connection,
            transaction))
        {
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = new HashSet<int>();
        await using (var command = new NpgsqlCommand(
            "SELECT version FROM registry.schema_migrations;",
            connection,
            transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in migrations.Where(item => !applied.Contains(item.Version)))
        {
            var sql = await ReadResourceAsync(migration.ResourceName, cancellationToken);
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var record = new NpgsqlCommand(
                """
                INSERT INTO registry.schema_migrations (version, name)
                VALUES (@version, @name);
                """,
                connection,
                transaction);
            record.Parameters.AddWithValue("version", migration.Version);
            record.Parameters.AddWithValue("name", migration.Name);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private IReadOnlyList<MigrationResource> DiscoverMigrations()
    {
        var resources = _assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal))
            .Select(ParseResource)
            .OrderBy(migration => migration.Version)
            .ToArray();

        if (resources.Length == 0)
        {
            throw new InvalidOperationException("No embedded PostgreSQL registry migrations were found.");
        }

        var duplicate = resources
            .GroupBy(migration => migration.Version)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL registry migration version {duplicate.Key} is duplicated.");
        }

        return resources;
    }

    private static MigrationResource ParseResource(string resourceName)
    {
        var markerIndex = resourceName.IndexOf(ResourceMarker, StringComparison.Ordinal);
        var fileName = resourceName[(markerIndex + ResourceMarker.Length)..];
        var match = MigrationFileName().Match(fileName);
        if (!match.Success || !int.TryParse(match.Groups["version"].Value, out var version))
        {
            throw new InvalidOperationException(
                $"Embedded PostgreSQL registry migration '{resourceName}' must use NNN_name.sql naming.");
        }

        return new MigrationResource(
            version,
            match.Groups["name"].Value,
            resourceName);
    }

    private async Task<string> ReadResourceAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        await using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded PostgreSQL registry migration '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    [GeneratedRegex("^(?<version>[0-9]{3})_(?<name>[A-Za-z0-9_]+)\\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileName();

    private sealed record MigrationResource(int Version, string Name, string ResourceName);
}
