using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

internal sealed class PostgreSqlOrganizationRegistryImportHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<HiveOptions> _options;
    private readonly ILogger<PostgreSqlOrganizationRegistryImportHostedService> _logger;
    private readonly IActionDomainContractRegistry _contractRegistry;

    public PostgreSqlOrganizationRegistryImportHostedService(
        IConfiguration configuration,
        IOptions<HiveOptions> options,
        ILogger<PostgreSqlOrganizationRegistryImportHostedService> logger,
        IActionDomainContractRegistry contractRegistry)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _contractRegistry = contractRegistry ?? throw new ArgumentNullException(nameof(contractRegistry));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogDebug(
                "Skipping organization configuration import because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        var configuredRoot = _options.Value.Organizations.RootPath;
        var organizationsRoot = Path.IsPathRooted(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.GetFullPath(configuredRoot, AppContext.BaseDirectory);

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var importer = new OrganizationConfigurationDirectoryImporter(
            new OrganizationConfigurationParser(),
            new OrganizationConfigurationImporter(registry),
            _contractRegistry);

        var results = await importer
            .ImportAsync(organizationsRoot, cancellationToken)
            .ConfigureAwait(false);
        foreach (var result in results)
        {
            _logger.LogInformation(
                "Organization {OrganizationId} registry import completed with status {ImportStatus} at version {ConfigurationVersion}.",
                result.Plan!.OrganizationId.Value,
                result.Status,
                result.Snapshot!.Version);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
