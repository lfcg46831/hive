using System.Text.Json;
using Hive.Connectors.GitHub;
using Hive.Domain.Connectors;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Infrastructure.Connectors;
using Hive.Infrastructure.Governance;
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

    [Theory]
    [InlineData("PT1S", true)]
    [InlineData("P1D", true)]
    [InlineData("PT0.999S", false)]
    [InlineData("30 seconds", false)]
    [InlineData(" PT30S", false)]
    [InlineData("", false)]
    public void Polling_interval_parser_accepts_only_trimmed_iso8601_values_of_at_least_one_second(
        string value,
        bool expected)
    {
        var parsed = GitHubIssuesConnectorOptionsValidator.TryParseInterval(
            value,
            out var interval);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.True(interval >= TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Options_validator_rejects_duplicate_instances_and_credentials_without_secret_values()
    {
        const string token = "duplicate-test-secret";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] = "configured",
            })
            .Build();
        var validator = new GitHubIssuesConnectorOptionsValidator(configuration);
        var instance = ValidInstanceOptions();
        var options = new GitHubIssuesConnectorOptions
        {
            Instances = [instance, ValidInstanceOptions()],
            Credentials =
            [
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = "acme-github",
                    Token = token,
                },
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = "acme-github",
                    Token = token,
                },
            ],
        };

        var result = validator.Validate(name: null, options);
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures);
        var diagnostic = string.Join("\n", failures);

        Assert.False(result.Succeeded);
        Assert.Contains("Credentials:1:InstanceId is declared more than once", diagnostic);
        Assert.Contains("Instances:1:InstanceId is declared more than once", diagnostic);
        Assert.DoesNotContain(token, diagnostic, StringComparison.Ordinal);
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

    [Fact]
    public void Plugin_contributes_tools_and_gate_contracts_only_through_generic_registries()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>());
        builder.Services.AddSingleton<IJourneyAuditLog>(NoopJourneyAuditLog.Instance);
        builder.Services.AddHiveActionDomainContracts();
        using var host = builder.Build();

        var tools = host.Services.GetRequiredService<IConnectorToolRegistry>();
        var contracts = host.Services.GetRequiredService<IActionDomainContractRegistry>();

        foreach (var operation in GitHubIssuesOutboundOperations.All)
        {
            var tool = Assert.IsAssignableFrom<IConnectorTool>(tools.Find(operation));
            Assert.Equal(operation, tool.Definition.Name);
            var contract = Assert.Single(
                contracts.ActionContracts,
                contract => contract.Action == Hive.Domain.Governance.ActionDomainActionKind.Tool
                    && contract.SelectorValue == operation);
            Assert.Contains(
                contract.Attributes,
                attribute => attribute.Name == GitHubIssuesActionAttributeNames.OperationType
                             && attribute.Source == Hive.Domain.Governance.ActionAttributeSource.Derived);
            Assert.Contains(
                contract.Attributes,
                attribute => attribute.Name == GitHubIssuesActionAttributeNames.Visibility
                             && attribute.Source == Hive.Domain.Governance.ActionAttributeSource.Derived);
            Assert.Single(
                contracts.ActionExtractors,
                extractor => extractor.Action == Hive.Domain.Governance.ActionDomainActionKind.Tool
                             && extractor.SelectorValue == operation);
        }
    }

    [Fact]
    public async Task Declared_polling_instance_requires_postgresql_but_empty_catalog_does_not()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Connectors:GitHubIssues:Instances:0:InstanceId"] = "acme-github",
            ["Hive:Connectors:GitHubIssues:Instances:0:OrganizationId"] = "acme-delivery",
            ["Hive:Connectors:GitHubIssues:Instances:0:Repositories:0"] = "acme/payments",
            ["Hive:Connectors:GitHubIssues:Instances:0:InboundDirectiveTarget"] = "bug-triage",
            ["Hive:Connectors:GitHubIssues:Instances:0:Polling:Interval"] = "PT30S",
            ["Hive:Connectors:GitHubIssues:Instances:0:Polling:PageSize"] = "100",
            ["Hive:Connectors:GitHubIssues:Credentials:0:InstanceId"] = "acme-github",
            ["Hive:Connectors:GitHubIssues:Credentials:0:Token"] = "test-token",
        }, includePostgreSql: false);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            "ConnectionStrings:PostgreSql is required",
            string.Join("\n", exception.Failures),
            StringComparison.Ordinal);
    }

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> configuration,
        bool includePostgreSql = true)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        var pluginConfiguration = new Dictionary<string, string?>
        {
            [$"{ConnectorPluginServiceCollectionExtensions.AssembliesSectionName}:0"] =
                typeof(GitHubIssuesConnectorPlugin).Assembly.GetName().Name,
        };
        if (includePostgreSql)
        {
            pluginConfiguration["ConnectionStrings:PostgreSql"] =
                "Host=localhost;Database=hive;Username=hive;Password=test-only";
        }

        builder.Configuration.AddInMemoryCollection(pluginConfiguration);
        builder.Services.AddHiveConnectorPlugins(builder.Configuration);
        return builder;
    }

    private static GitHubIssuesConnectorInstanceOptions ValidInstanceOptions() =>
        new()
        {
            InstanceId = "acme-github",
            OrganizationId = "acme-delivery",
            Repositories = ["acme/payments"],
            InboundDirectiveTarget = "bug-triage",
            OutboundOperations = [GitHubIssuesOutboundOperations.Comment],
            Polling = new GitHubIssuesPollingOptions
            {
                Interval = "PT30S",
                PageSize = 100,
            },
        };
}
