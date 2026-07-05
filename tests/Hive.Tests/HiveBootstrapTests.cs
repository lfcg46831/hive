using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Scheduling.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class HiveBootstrapTests
{
    [Fact]
    public async Task Bootstrap_binds_roles_and_preserves_configured_values()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = " API ",
            ["Hive:Node:Roles:1"] = "Agents",
        });
        using var host = builder.Build();

        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<HiveOptions>>().Value;
        await host.StopAsync();

        Assert.Equal(new[] { " API ", "Agents" }, options.Node.Roles);
    }

    [Fact]
    public async Task Bootstrap_rejects_invalid_roles_when_host_starts()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
            ["Hive:Node:Roles:1"] = "API",
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Hive:Node:Roles") && failure.Contains("duplicate role values"));
    }

    [Fact]
    public void Bootstrap_registers_scheduler_delivery_migration_before_role_workloads()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
        });
        using var host = builder.Build();

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        var schedulerMigration = Array.FindIndex(
            hostedServices,
            service => service.GetType() == typeof(PostgreSqlSchedulerPulseDeliveryMigrationHostedService));
        var roleWorkloads = Array.FindIndex(
            hostedServices,
            service => service.GetType() == typeof(RoleWorkloadHostedService));

        Assert.True(schedulerMigration >= 0);
        Assert.True(roleWorkloads >= 0);
        Assert.True(schedulerMigration < roleWorkloads);
    }

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        return builder;
    }
}
