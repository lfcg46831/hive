using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.OccupantChannels;
using Hive.Infrastructure.OccupantChannels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class ImapInboundEmailBootstrapTests
{
    [Fact]
    public void Enabled_source_composes_transport_poller_and_postgresql_store()
    {
        var builder = CreateBuilder(ValidConfiguration(NodeRoleNames.Connectors));
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
    }

    [Fact]
    public async Task Connector_node_rejects_incomplete_source_and_missing_durable_store()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = NodeRoleNames.Connectors,
            ["Hive:OccupantChannels:Email:Imap:Enabled"] = "true",
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

    [Fact]
    public async Task Non_connector_node_does_not_require_imap_credentials_or_postgresql()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = NodeRoleNames.Api,
            ["Hive:OccupantChannels:Email:Imap:Enabled"] = "true",
        });
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
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
