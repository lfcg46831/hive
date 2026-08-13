using Hive.Domain.Connectors;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class ConnectorPluginArchitectureTests
{
    [Fact]
    public void Common_infrastructure_has_no_reference_to_a_concrete_connector_assembly()
    {
        var assemblyReferences = typeof(HiveBootstrapExtensions)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(
            assemblyReferences,
            name => name.StartsWith("Hive.Connectors.", StringComparison.Ordinal));

        var bootstrapSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Infrastructure",
            "Configuration",
            "HiveBootstrapExtensions.cs"));
        Assert.DoesNotContain("Hive.Connectors.", bootstrapSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Main_solution_and_test_project_have_no_concrete_connector_references()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "Hive.sln"));
        var testProject = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tests",
            "Hive.Tests",
            "Hive.Tests.csproj"));

        Assert.DoesNotContain("Hive.Connectors.", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("Hive.Connectors.", testProject, StringComparison.Ordinal);
    }

    [Fact]
    public void No_configured_plugin_is_valid_and_inert()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddHiveConnectorPlugins(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetRequiredService<ConnectorPluginCatalog>().Plugins);
        Assert.Empty(provider.GetServices<IConnectorPlugin>());
    }

    [Fact]
    public void Common_bootstrap_discovers_configured_assembly_without_a_concrete_registration_call()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ConnectorPluginServiceCollectionExtensions.AssembliesSectionName}:0"] =
                typeof(SyntheticConnectorPlugin).Assembly.GetName().Name,
        });

        builder.AddHiveBootstrap();

        using var host = builder.Build();
        var descriptor = Assert.Single(
            host.Services.GetRequiredService<ConnectorPluginCatalog>().Plugins);
        Assert.Equal("synthetic", descriptor.Id.Value);
        Assert.IsType<SyntheticConnectorPlugin>(
            Assert.Single(host.Services.GetServices<IConnectorPlugin>()));
    }

    [Theory]
    [InlineData("plugins/Hive.Connectors.Example.dll")]
    [InlineData("Hive.Connectors.Example, Version=1.0.0.0")]
    [InlineData(" Hive.Connectors.Example")]
    public void Assembly_paths_and_display_names_fail_closed(string configuredName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ConnectorPluginServiceCollectionExtensions.AssembliesSectionName}:0"] =
                    configuredName,
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddHiveConnectorPlugins(configuration));

        Assert.Contains("simple name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_plugin_assembly_fails_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ConnectorPluginServiceCollectionExtensions.AssembliesSectionName}:0"] =
                    "Hive.Connectors.Missing",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddHiveConnectorPlugins(configuration));

        Assert.Contains("could not be loaded", exception.Message, StringComparison.Ordinal);
    }

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
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

public sealed class SyntheticConnectorPlugin : IConnectorPlugin
{
    public ConnectorId Id { get; } = ConnectorId.From("synthetic");

    public void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
    }
}
