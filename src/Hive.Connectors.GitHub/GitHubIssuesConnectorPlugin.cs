using Hive.Domain.Connectors;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Connectors;
using Hive.Infrastructure.Hosting;
using Hive.Connectors.GitHub.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub;

public sealed class GitHubIssuesConnectorPlugin : IConnectorPlugin
{
    public ConnectorId Id { get; } = ConnectorId.From("github-issues");

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<GitHubIssuesConnectorOptions>,
            GitHubIssuesConnectorOptionsValidator>();
        services
            .AddOptions<GitHubIssuesConnectorOptions>()
            .Bind(configuration.GetSection(GitHubIssuesConnectorOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider =>
            new GitHubIssuesConnectorConfigurationCatalog(
                serviceProvider.GetRequiredService<IOptions<GitHubIssuesConnectorOptions>>()));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IGitHubIssuesInboundClient>(
            UnavailableGitHubIssuesInboundClient.Instance);
        services.TryAddSingleton<IGitHubIssuesInboundStore>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);
            return string.IsNullOrWhiteSpace(connectionString)
                ? UnavailableGitHubIssuesInboundStore.Instance
                : new PostgreSqlGitHubIssuesInboundStore(connectionString);
        });
        services.TryAddSingleton<IGitHubIssuesInboundPoller, GitHubIssuesInboundPoller>();
        services.TryAddSingleton<DirectiveRoutingValidator>();
        services.TryAddSingleton<IGitHubIssuesInboundProcessor, GitHubIssuesInboundProcessor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IRoleWorkload,
            GitHubIssuesInboundSingletonWorkload>());
    }
}
