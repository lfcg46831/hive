using Hive.Actors;
using Hive.Actors.OccupantChannels;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.OccupantChannels;
using Hive.Infrastructure.OccupantChannels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class ImapInboundEmailBootstrapTests
{
    [Theory]
    [InlineData(NodeRoleNames.Connectors)]
    [InlineData(" CONNECTORS ")]
    public void Enabled_source_composes_transport_poller_and_postgresql_store(string role)
    {
        var builder = CreateBuilder(ValidConfiguration(role));
        using var host = builder.Build();

        Assert.IsType<MailKitImapInboundEmailClient>(
            host.Services.GetRequiredService<IImapInboundEmailClient>());
        Assert.IsType<ImapInboundEmailPoller>(
            host.Services.GetRequiredService<IImapInboundEmailPoller>());
        Assert.IsType<InboundOccupantEmailParser>(
            host.Services.GetRequiredService<IInboundOccupantEmailParser>());
        Assert.IsType<InboundOccupantEmailProcessor>(
            host.Services.GetRequiredService<IInboundOccupantEmailProcessor>());
        Assert.IsType<PostgreSqlImapInboundEmailStore>(
            host.Services.GetRequiredService<IImapInboundEmailStore>());
        Assert.IsType<HmacOccupantChannelCorrelationTokenService>(
            host.Services.GetRequiredService<IOccupantChannelCorrelationTokenService>());
        var workload = Assert.Single(host.Services.GetServices<IRoleWorkload>()
            .OfType<ImapInboundEmailSingletonWorkload>());
        Assert.True(workload.IsEnabled);
    }

    [Fact]
    public async Task Connector_node_rejects_incomplete_source_and_missing_durable_store()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = NodeRoleNames.Connectors,
            ["Hive:OccupantChannels:Email:Imap:Enabled"] = "true",
            ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
                OccupantChannelCorrelationTokenTests.SigningKey(),
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Imap:Host", StringComparison.Ordinal));
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Imap:Username", StringComparison.Ordinal));
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("ConnectionStrings:PostgreSql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connector_node_rejects_unstable_source_identity_and_unsafe_limits()
    {
        var configuration = ValidConfiguration(NodeRoleNames.Connectors);
        configuration["Hive:OccupantChannels:Email:Imap:SourceId"] = "Reply Mailbox";
        configuration["Hive:OccupantChannels:Email:Imap:BatchSize"] = "0";
        configuration["Hive:OccupantChannels:Email:Imap:PollInterval"] = "00:00:00.500";
        var builder = CreateBuilder(configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(exception.Failures, failure => failure.Contains("SourceId", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("BatchSize", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("PollInterval", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NodeRoleNames.Agents, false)]
    [InlineData(NodeRoleNames.Api, false)]
    [InlineData(NodeRoleNames.Gateway, false)]
    [InlineData(NodeRoleNames.Connectors, false)]
    [InlineData("agents,api,gateway,connectors", false)]
    [InlineData(NodeRoleNames.Agents, true)]
    [InlineData(NodeRoleNames.Api, true)]
    [InlineData(NodeRoleNames.Gateway, true)]
    public async Task Inactive_source_starts_actor_host_without_email_dependencies(
        string roles,
        bool enabled)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Hive:OccupantChannels:Email:Imap:Enabled"] = enabled.ToString(),
        };
        var roleNames = roles.Split(',');
        for (var index = 0; index < roleNames.Length; index++)
        {
            configuration[$"Hive:Node:Roles:{index}"] = roleNames[index];
        }

        var builder = CreateBuilder(configuration);
        using var host = builder.Build();

        await host.StartAsync();
        try
        {
            Assert.Null(host.Services.GetService<IOccupantChannelCorrelationTokenService>());
            Assert.Null(host.Services.GetService<IInboundOccupantEmailParser>());
            Assert.Null(host.Services.GetService<IImapInboundEmailPoller>());
            Assert.Null(host.Services.GetService<ImapInboundEmailSingletonWorkload>());
            Assert.DoesNotContain(
                host.Services.GetServices<IHostedService>()
                    .OfType<RoleWorkloadHostedService>().Single().StartedWorkloads,
                workload => workload is ImapInboundEmailSingletonWorkload);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("c2hvcnQ=")]
    public async Task Enabled_imap_rejects_missing_or_invalid_signing_key_at_startup(string? signingKey)
    {
        var configuration = ValidConfiguration(NodeRoleNames.Connectors);
        configuration["Hive:OccupantChannels:CorrelationTokens:SigningKey"] = signingKey;
        var builder = CreateBuilder(configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(exception.Failures,
            failure => failure.Contains("CorrelationTokens:SigningKey", StringComparison.Ordinal));
    }

    private static HostApplicationBuilder CreateBuilder(
        IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        // Exercise the validation used by Development hosts as well as real workload activation.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = GetFreeTcpPort().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        return builder;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
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

    private static Dictionary<string, string?> ValidConfiguration(string role) => new()
    {
        ["Hive:Node:Roles:0"] = role,
        ["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=hive;Username=hive;Password=test-only",
        ["Hive:OccupantChannels:Email:Imap:Enabled"] = "true",
        ["Hive:OccupantChannels:Email:Imap:SourceId"] = "occupant-replies",
        ["Hive:OccupantChannels:Email:Imap:Host"] = "imap.example.test",
        ["Hive:OccupantChannels:Email:Imap:Port"] = "993",
        ["Hive:OccupantChannels:Email:Imap:Security"] = "ssl-on-connect",
        ["Hive:OccupantChannels:Email:Imap:Username"] = "hive",
        ["Hive:OccupantChannels:Email:Imap:Password"] = "test-only-secret",
        ["Hive:OccupantChannels:Email:Imap:Mailbox"] = "INBOX",
        ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
            OccupantChannelCorrelationTokenTests.SigningKey(),
    };
}
