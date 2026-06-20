using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class RoleWorkloadHostedServiceTests
{
    [Fact]
    public async Task Starts_only_workloads_whose_role_is_active()
    {
        var agents = new FakeRoleWorkload(NodeRoleNames.Agents);
        var gateway = new FakeRoleWorkload(NodeRoleNames.Gateway);
        var api = new FakeRoleWorkload(NodeRoleNames.Api);
        var service = CreateService(
            new[] { NodeRoleNames.Agents, NodeRoleNames.Gateway },
            agents, gateway, api);

        await service.StartAsync(CancellationToken.None);

        Assert.True(agents.IsRunning);
        Assert.True(gateway.IsRunning);
        Assert.False(api.IsRunning);
        Assert.Equal(new IRoleWorkload[] { agents, gateway }, service.StartedWorkloads);
    }

    [Fact]
    public async Task All_in_one_node_starts_every_role()
    {
        var workloads = NodeRoleNames.All
            .Select(role => new FakeRoleWorkload(role))
            .ToArray();
        var service = CreateService(NodeRoleNames.All.ToArray(), workloads);

        await service.StartAsync(CancellationToken.None);

        Assert.All(workloads, workload => Assert.True(workload.IsRunning));
        Assert.Equal(workloads.Length, service.StartedWorkloads.Count);
    }

    [Fact]
    public async Task Inactive_role_workload_is_never_started()
    {
        var connectors = new FakeRoleWorkload(NodeRoleNames.Connectors);
        var service = CreateService(new[] { NodeRoleNames.Api }, connectors);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(0, connectors.StartCount);
        Assert.Empty(service.StartedWorkloads);
    }

    [Fact]
    public async Task Stops_started_workloads_in_reverse_order()
    {
        var log = new List<string>();
        var agents = new FakeRoleWorkload(NodeRoleNames.Agents, log);
        var gateway = new FakeRoleWorkload(NodeRoleNames.Gateway, log);
        var service = CreateService(
            new[] { NodeRoleNames.Agents, NodeRoleNames.Gateway },
            agents, gateway);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "start:agents", "start:gateway", "stop:gateway", "stop:agents" },
            log);
    }

    private static RoleWorkloadHostedService CreateService(
        string[] activeRoles,
        params FakeRoleWorkload[] workloads)
    {
        var options = Options.Create(
            new HiveOptions { Node = new NodeOptions { Roles = activeRoles } });

        return new RoleWorkloadHostedService(
            workloads,
            new ActiveNodeRoles(options),
            NullLogger<RoleWorkloadHostedService>.Instance);
    }
}
