using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class OrganizationConfigurationDirectoryImporterTests
{
    [Fact]
    public async Task Directory_importer_materializes_each_declared_organization()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry));

        var results = await importer.ImportAsync(
            Path.Combine(RepositoryRoot, "config", "organizations"));

        var result = Assert.Single(results);
        Assert.Equal(OrganizationImportStatus.Applied, result.Status);
        Assert.Equal("acme-delivery", result.Snapshot!.OrganizationId.Value);
        Assert.Equal(2, result.Snapshot.Units.Count);
        Assert.Equal(2, result.Snapshot.Positions.Count);
        Assert.Single(result.Snapshot.Schedules);
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
