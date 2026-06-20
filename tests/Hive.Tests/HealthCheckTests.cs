using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class HealthCheckTests
{
    [Fact]
    public async Task Process_liveness_is_always_healthy()
    {
        var result = await new ProcessLivenessHealthCheck()
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Configuration_check_is_healthy_when_a_role_is_active()
    {
        var check = new ConfigurationHealthCheck(ActiveRoles(NodeRoleNames.Api));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Configuration_check_is_unhealthy_when_no_role_is_active()
    {
        var check = new ConfigurationHealthCheck(ActiveRoles());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Hive:Node:Roles", result.Description);
    }

    [Fact]
    public async Task Dependencies_check_is_unhealthy_when_connection_string_is_missing()
    {
        var check = new RequiredDependenciesHealthCheck(Configuration(connectionString: ""));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(ConnectionStringNames.PostgreSql, result.Description);
    }

    [Fact]
    public async Task Dependencies_check_is_healthy_when_connection_string_is_present()
    {
        var check = new RequiredDependenciesHealthCheck(
            Configuration(connectionString: "Host=localhost;Database=hive"));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Bootstrap_registers_the_three_minimal_checks_with_live_and_ready_tags()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
            ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
        });

        var report = await host.Services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(
            new[]
            {
                HiveHealthChecks.ConfigurationName,
                HiveHealthChecks.DependenciesName,
                HiveHealthChecks.ProcessName,
            },
            report.Entries.Keys.OrderBy(name => name).ToArray());

        Assert.Contains(HiveHealthChecks.LiveTag, report.Entries[HiveHealthChecks.ProcessName].Tags);
        Assert.Contains(HiveHealthChecks.ReadyTag, report.Entries[HiveHealthChecks.ConfigurationName].Tags);
        Assert.Contains(HiveHealthChecks.ReadyTag, report.Entries[HiveHealthChecks.DependenciesName].Tags);
    }

    [Fact]
    public async Task Bootstrap_readiness_is_unhealthy_until_mandatory_dependencies_are_configured()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
        });

        var report = await host.Services
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Tags.Contains(HiveHealthChecks.ReadyTag));

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(
            HealthStatus.Unhealthy,
            report.Entries[HiveHealthChecks.DependenciesName].Status);
    }

    private static IHost BuildHost(IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        return builder.Build();
    }

    private static ActiveNodeRoles ActiveRoles(params string[] roles) =>
        new(Options.Create(new HiveOptions { Node = new NodeOptions { Roles = roles } }));

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] = connectionString,
            })
            .Build();
}
