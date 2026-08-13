using Hive.Domain.Connectors;
using Hive.Infrastructure.Connectors;
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
    }
}
