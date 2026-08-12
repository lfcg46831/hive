using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class SmtpOccupantChannelBootstrapTests
{
    [Fact]
    public void Enabled_smtp_adapter_is_registered_on_connectors_role()
    {
        var builder = CreateBuilder(ValidConfiguration(NodeRoleNames.Connectors));
        using var host = builder.Build();

        Assert.IsType<SmtpOccupantChannel>(
            host.Services.GetRequiredService<IOccupantChannel>());
        Assert.IsType<SignedOccupantChannelDeliveryRequestFactory>(
            host.Services.GetRequiredService<IOccupantChannelDeliveryRequestFactory>());
        Assert.IsType<HmacOccupantChannelCorrelationTokenService>(
            host.Services.GetRequiredService<IOccupantChannelCorrelationTokenService>());
    }

    [Fact]
    public void Enabled_smtp_adapter_is_not_registered_outside_connectors_role()
    {
        var builder = CreateBuilder(ValidConfiguration(NodeRoleNames.Api));
        using var host = builder.Build();

        Assert.Null(host.Services.GetService<IOccupantChannel>());
    }

    [Fact]
    public async Task Connector_node_rejects_incomplete_smtp_configuration_at_startup()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = NodeRoleNames.Connectors,
            ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
                OccupantChannelCorrelationTokenTests.SigningKey(),
            ["Hive:OccupantChannels:Email:Smtp:Enabled"] = "true",
        });
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Smtp:Host", StringComparison.Ordinal));
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Smtp:FromAddress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connector_node_rejects_missing_correlation_signing_key_at_startup()
    {
        var configuration = ValidConfiguration(NodeRoleNames.Connectors);
        configuration.Remove("Hive:OccupantChannels:CorrelationTokens:SigningKey");
        var builder = CreateBuilder(configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("CorrelationTokens:SigningKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connector_node_rejects_short_correlation_signing_key_at_startup()
    {
        var configuration = ValidConfiguration(NodeRoleNames.Connectors);
        configuration["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
            Convert.ToBase64String(new byte[16]);
        var builder = CreateBuilder(configuration);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("at least 32 bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_connector_node_does_not_require_smtp_secrets_or_open_a_transport()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = NodeRoleNames.Api,
            ["Hive:OccupantChannels:Email:Smtp:Enabled"] = "true",
        });
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Null(host.Services.GetService<IOccupantChannel>());
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
        ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
            OccupantChannelCorrelationTokenTests.SigningKey(),
        ["Hive:OccupantChannels:Email:Smtp:Enabled"] = "true",
        ["Hive:OccupantChannels:Email:Smtp:Host"] = "smtp.example.test",
        ["Hive:OccupantChannels:Email:Smtp:Port"] = "587",
        ["Hive:OccupantChannels:Email:Smtp:Security"] = "start-tls",
        ["Hive:OccupantChannels:Email:Smtp:FromAddress"] = "hive@example.test",
        ["Hive:OccupantChannels:Email:Smtp:ReplyToAddress"] = "replies@example.test",
        ["Hive:OccupantChannels:Email:Smtp:Username"] = "hive",
        ["Hive:OccupantChannels:Email:Smtp:Password"] = "test-only-secret",
    };
}
