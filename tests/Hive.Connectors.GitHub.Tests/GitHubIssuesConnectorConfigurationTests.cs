using System.Text.Json;
using Hive.Connectors.GitHub;
using Hive.Domain.Connectors;
using Hive.Domain.Identity;
using Hive.Infrastructure.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesConnectorConfigurationTests
{
    [Fact]
    public void Declarative_instance_is_immutable_scoped_and_credential_free()
    {
        var repositories = new List<string> { "acme/payments", "acme/orders" };
        var operations = new List<string>
        {
            GitHubIssuesOutboundOperations.Comment,
            GitHubIssuesOutboundOperations.UpdateState,
        };

        var instance = new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            OrganizationId.From("acme-delivery"),
            repositories,
            PositionId.From("bug-triage"),
            operations,
            new GitHubIssuesPollingConfiguration(TimeSpan.FromSeconds(30), pageSize: 100));

        repositories.Clear();
        operations.Clear();

        Assert.Equal("acme-github", instance.InstanceId);
        Assert.Equal("acme-delivery", instance.OrganizationId.Value);
        Assert.Equal(["acme/payments", "acme/orders"], instance.Repositories);
        Assert.Equal("bug-triage", instance.InboundDirectiveTarget.Value);
        Assert.Equal(
            [GitHubIssuesOutboundOperations.Comment, GitHubIssuesOutboundOperations.UpdateState],
            instance.OutboundOperations);
        Assert.Equal(TimeSpan.FromSeconds(30), instance.Polling.Interval);
        Assert.Equal(100, instance.Polling.PageSize);
        Assert.DoesNotContain(
            typeof(GitHubIssuesConnectorInstanceConfiguration).GetProperties(),
            property => property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(GitHubIssuesConnectorInstanceConfiguration).Assembly
                .GetExportedTypes()
                .Where(type => type.Namespace == typeof(GitHubIssuesConnectorInstanceConfiguration).Namespace)
                .SelectMany(type => type.GetProperties()),
            property => property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Declarative_instance_rejects_ambiguous_scope_and_polling_values()
    {
        var organizationId = OrganizationId.From("acme-delivery");
        var target = PositionId.From("bug-triage");
        var polling = new GitHubIssuesPollingConfiguration(TimeSpan.FromSeconds(30), 100);

        Assert.Throws<ArgumentException>(() => new GitHubIssuesConnectorInstanceConfiguration(
            "Acme GitHub",
            organizationId,
            ["acme/payments"],
            target,
            [],
            polling));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            organizationId,
            [],
            target,
            [],
            polling));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            organizationId,
            ["acme/payments", "ACME/PAYMENTS"],
            target,
            [],
            polling));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            organizationId,
            ["not-a-repository"],
            target,
            [],
            polling));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            organizationId,
            ["acme/payments"],
            target,
            ["issues.delete"],
            polling));
        Assert.Throws<ArgumentException>(() => new GitHubIssuesConnectorInstanceConfiguration(
            "acme-github",
            organizationId,
            ["acme/payments"],
            target,
            [GitHubIssuesOutboundOperations.Comment, GitHubIssuesOutboundOperations.Comment],
            polling));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GitHubIssuesPollingConfiguration(TimeSpan.Zero, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GitHubIssuesPollingConfiguration(TimeSpan.FromSeconds(1), 101));
    }

    [Fact]
    public void Public_schema_declares_all_instance_fields_and_directional_scopes_without_credentials()
    {
        var contract = GitHubIssuesConnectorConfigurationSchema.Instance;
        var schema = contract.Schema;

        Assert.Equal(1, contract.Version);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            [
                "instance_id",
                "organization_id",
                "repositories",
                "inbound_directive_target",
                "outbound_operations",
                "polling",
            ],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.Equal(2, contract.Scopes.Count);
        Assert.Equal(ConnectorScopeDirection.Both, contract.Scopes[0].Direction);
        Assert.Equal("$.repositories", contract.Scopes[0].ConfigurationPath);
        Assert.Equal(ConnectorScopeDirection.Outbound, contract.Scopes[1].Direction);
        Assert.Equal("$.outbound_operations", contract.Scopes[1].ConfigurationPath);

        var rendered = schema.GetRawText();
        Assert.DoesNotContain("token", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Host_binds_validates_and_projects_instances_while_resolving_secret_separately()
    {
        const string token = "test-only-github-token";
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Connectors:GitHubIssues:Instances:0:InstanceId"] = "acme-github",
            ["Hive:Connectors:GitHubIssues:Instances:0:OrganizationId"] = "acme-delivery",
            ["Hive:Connectors:GitHubIssues:Instances:0:Repositories:0"] = "acme/payments",
            ["Hive:Connectors:GitHubIssues:Instances:0:Repositories:1"] = "acme/orders",
            ["Hive:Connectors:GitHubIssues:Instances:0:InboundDirectiveTarget"] = "bug-triage",
            ["Hive:Connectors:GitHubIssues:Instances:0:OutboundOperations:0"] =
                GitHubIssuesOutboundOperations.Comment,
            ["Hive:Connectors:GitHubIssues:Instances:0:Polling:Interval"] = "PT30S",
            ["Hive:Connectors:GitHubIssues:Instances:0:Polling:PageSize"] = "100",
            ["Hive:Connectors:GitHubIssues:Credentials:0:InstanceId"] = "acme-github",
            ["Hive:Connectors:GitHubIssues:Credentials:0:Token"] = token,
        });
        using var host = builder.Build();

        await host.StartAsync();
        var plugins = host.Services.GetRequiredService<ConnectorPluginCatalog>();
        var catalog = host.Services.GetRequiredService<GitHubIssuesConnectorConfigurationCatalog>();
        await host.StopAsync();

        var plugin = Assert.Single(plugins.Plugins);
        Assert.Equal("github-issues", plugin.Id.Value);
        Assert.Equal("Hive.Connectors.GitHub", plugin.AssemblyName);
        var instance = Assert.Single(catalog.Instances);
        Assert.Equal("acme-delivery", instance.OrganizationId.Value);
        Assert.Equal("bug-triage", instance.InboundDirectiveTarget.Value);
        Assert.Equal(TimeSpan.FromSeconds(30), instance.Polling.Interval);
        Assert.Equal(token, catalog.GetToken("acme-github"));
        Assert.DoesNotContain(
            token,
            JsonSerializer.Serialize(instance),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_fails_closed_for_invalid_scope_polling_or_missing_secret_without_leaking_values()
    {
        const string orphanToken = "must-not-appear-in-diagnostics";
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Connectors:GitHubIssues:Instances:0:InstanceId"] = "acme-github",
            ["Hive:Connectors:GitHubIssues:Instances:0:Repositories:0"] = "not-a-repository",
            ["Hive:Connectors:GitHubIssues:Instances:0:OutboundOperations:0"] = "issues.delete",
            ["Hive:Connectors:GitHubIssues:Instances:0:Polling:Interval"] = "30 seconds",
            ["Hive:Connectors:GitHubIssues:Instances:0:Polling:PageSize"] = "101",
            ["Hive:Connectors:GitHubIssues:Credentials:0:InstanceId"] = "orphan",
            ["Hive:Connectors:GitHubIssues:Credentials:0:Token"] = orphanToken,
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());
        var diagnostic = string.Join("\n", exception.Failures);

        Assert.Contains("OrganizationId", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Repositories:0", diagnostic, StringComparison.Ordinal);
        Assert.Contains("InboundDirectiveTarget", diagnostic, StringComparison.Ordinal);
        Assert.Contains("OutboundOperations:0", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Polling:Interval", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Polling:PageSize", diagnostic, StringComparison.Ordinal);
        Assert.Contains("missing an operational secret", diagnostic, StringComparison.Ordinal);
        Assert.Contains("undeclared instance 'orphan'", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(orphanToken, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_connector_catalog_is_valid_and_inert()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>());
        using var host = builder.Build();

        await host.StartAsync();
        var catalog = host.Services.GetRequiredService<GitHubIssuesConnectorConfigurationCatalog>();
        await host.StopAsync();

        Assert.Empty(catalog.Instances);
    }

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ConnectorPluginServiceCollectionExtensions.AssembliesSectionName}:0"] =
                typeof(GitHubIssuesConnectorPlugin).Assembly.GetName().Name,
        });
        builder.Services.AddHiveConnectorPlugins(builder.Configuration);
        return builder;
    }
}
