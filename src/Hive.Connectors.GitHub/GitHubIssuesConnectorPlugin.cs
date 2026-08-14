using Hive.Domain.Connectors;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Connectors;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Governance;
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
            IActionDomainContractSource,
            GitHubIssuesActionDomainContractSource>());
        services.TryAddSingleton<IGitHubIssuesOutboundClient>(
            UnavailableGitHubIssuesOutboundClient.Instance);
        services.TryAddSingleton<IGitHubIssuesOutboundStore>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);
            return string.IsNullOrWhiteSpace(connectionString)
                ? UnavailableGitHubIssuesOutboundStore.Instance
                : new PostgreSqlGitHubIssuesOutboundStore(connectionString);
        });
        services.TryAddSingleton<IGitHubIssuesOutboundBackoff, GitHubIssuesOutboundBackoff>();
        services.TryAddSingleton<IGitHubIssuesOutboundExecutor, GitHubIssuesOutboundExecutor>();
        services.AddSingleton<IConnectorTool>(serviceProvider =>
            new GitHubIssuesOutboundTool(
                GitHubIssuesOutboundOperations.Comment,
                serviceProvider.GetRequiredService<IGitHubIssuesOutboundExecutor>()));
        services.AddSingleton<IConnectorTool>(serviceProvider =>
            new GitHubIssuesOutboundTool(
                GitHubIssuesOutboundOperations.UpdateState,
                serviceProvider.GetRequiredService<IGitHubIssuesOutboundExecutor>()));
        services.AddSingleton<IConnectorTool>(serviceProvider =>
            new GitHubIssuesOutboundTool(
                GitHubIssuesOutboundOperations.UpdateLabels,
                serviceProvider.GetRequiredService<IGitHubIssuesOutboundExecutor>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IRoleWorkload,
            GitHubIssuesInboundSingletonWorkload>());
    }
}
