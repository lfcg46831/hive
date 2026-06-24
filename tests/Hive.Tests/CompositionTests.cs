using Akka.Actor;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Hive.Tests.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class CompositionTests(PostgreSqlFixture postgreSql)
{
    public static TheoryData<string> ExecutableNames => new()
    {
        "api",
        "worker",
    };

    [Fact]
    public async Task Api_entry_point_starts_with_required_services_and_api_role()
    {
        await using var app = global::Hive.Api.Program.Build(CreateApiArgs(
            roles: [NodeRoleNames.Api],
            includePostgreSql: true));

        await app.StartAsync();
        try
        {
            Assert.NotNull(app.Services.GetRequiredService<ActorSystem>());
            Assert.Equal(
                new[] { NodeRoleNames.Api },
                app.Services.GetRequiredService<ActiveNodeRoles>().Values);
            Assert.NotNull(app.Services.GetRequiredService<NodeDiagnosticsProvider>());

            var readiness = await app.Services
                .GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(registration =>
                    registration.Tags.Contains(HiveHealthChecks.ReadyTag));

            Assert.Equal(HealthStatus.Healthy, readiness.Status);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Worker_entry_point_starts_with_required_services_and_backend_roles()
    {
        using var host = global::Hive.Worker.Program.Build(CreateWorkerArgs(
            roles:
            [
                NodeRoleNames.Agents,
                NodeRoleNames.Gateway,
                NodeRoleNames.Connectors,
            ],
            includePostgreSql: true));

        await host.StartAsync();
        try
        {
            Assert.NotNull(host.Services.GetRequiredService<ActorSystem>());
            Assert.Equal(
                new[]
                {
                    NodeRoleNames.Agents,
                    NodeRoleNames.Connectors,
                    NodeRoleNames.Gateway,
                },
                host.Services
                    .GetRequiredService<ActiveNodeRoles>()
                    .Values
                    .OrderBy(role => role));
            Assert.NotNull(host.Services.GetRequiredService<NodeDiagnosticsProvider>());

            var readiness = await host.Services
                .GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(registration =>
                    registration.Tags.Contains(HiveHealthChecks.ReadyTag));

            Assert.Equal(HealthStatus.Healthy, readiness.Status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ExecutableNames))]
    public async Task Entry_point_rejects_empty_required_roles(string executableName)
    {
        using var host = BuildHost(
            executableName,
            roles: [string.Empty],
            includePostgreSql: true);

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Hive:Node:Roles", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ExecutableNames))]
    public async Task Entry_point_reports_not_ready_when_PostgreSql_config_is_missing(
        string executableName)
    {
        using var host = BuildHost(
            executableName,
            roles: RolesFor(executableName),
            includePostgreSql: false);

        await host.StartAsync();
        try
        {
            var readiness = await host.Services
                .GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(registration =>
                    registration.Tags.Contains(HiveHealthChecks.ReadyTag));

            Assert.Equal(HealthStatus.Unhealthy, readiness.Status);
            Assert.Equal(
                HealthStatus.Unhealthy,
                readiness.Entries[HiveHealthChecks.DependenciesName].Status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private IHost BuildHost(
        string executableName,
        IReadOnlyList<string> roles,
        bool includePostgreSql) => executableName switch
        {
            "api" => global::Hive.Api.Program.Build(
                CreateApiArgs(roles, includePostgreSql)),
            "worker" => global::Hive.Worker.Program.Build(
                CreateWorkerArgs(roles, includePostgreSql)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(executableName),
                executableName,
                "Unknown executable."),
        };

    private static string[] RolesFor(string executableName) => executableName switch
    {
        "api" => [NodeRoleNames.Api],
        "worker" =>
        [
            NodeRoleNames.Agents,
            NodeRoleNames.Gateway,
            NodeRoleNames.Connectors,
        ],
        _ => throw new ArgumentOutOfRangeException(
            nameof(executableName),
            executableName,
            "Unknown executable."),
    };

    private string[] CreateApiArgs(
        IReadOnlyList<string> roles,
        bool includePostgreSql)
    {
        var args = CreateCommonArgs(roles, includePostgreSql);
        args.Add($"--contentRoot={Path.Combine(RepositoryRoot, "src", "Hive.Api")}");
        args.Add("--urls=http://127.0.0.1:0");
        return args.ToArray();
    }

    private string[] CreateWorkerArgs(
        IReadOnlyList<string> roles,
        bool includePostgreSql)
    {
        var args = CreateCommonArgs(roles, includePostgreSql);
        args.Add($"--contentRoot={Path.Combine(RepositoryRoot, "src", "Hive.Worker")}");
        return args.ToArray();
    }

    private List<string> CreateCommonArgs(
        IReadOnlyList<string> roles,
        bool includePostgreSql)
    {
        var args = new List<string>
        {
            "--Hive:Cluster:Hostname=127.0.0.1",
            $"--Hive:Cluster:Port={GetFreeTcpPort()}",
        };

        for (var index = 0; index < roles.Count; index++)
        {
            args.Add($"--Hive:Node:Roles:{index}={roles[index]}");
        }

        if (includePostgreSql)
        {
            args.Add($"--ConnectionStrings:PostgreSql={postgreSql.ConnectionString}");
        }

        return args;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
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
