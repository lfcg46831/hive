using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class AiGatewayBootstrapTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task AddHiveAiGateway_registers_default_executable_gateway()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGateway();

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        Assert.IsType<AiGateway>(gateway);
        Assert.NotNull(provider.GetRequiredService<IAiGatewayAuditPublisher>());
        Assert.NotNull(provider.GetRequiredService<IAiGatewayDetailedAuditPublisher>());
        Assert.Null(provider.GetService<IChatClient>());

        var response = await gateway.CompleteAsync(Request());

        Assert.False(response.IsSuccess);
        Assert.True(response.IsFailure);
        Assert.NotNull(response.Error);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, response.Error.Code);
        Assert.Equal("AI gateway provider is not configured.", response.Error.Message);
        Assert.False(response.Error.IsRetryable);
        Assert.Equal(Organization, response.Error.OrganizationId);
        Assert.Equal(Position, response.Error.PositionId);
        Assert.Equal(Thread, response.Error.ThreadId);
        Assert.Equal(Message, response.Error.MessageId);
        Assert.Null(response.Error.Provider);
    }

    [Fact]
    public async Task AddHiveAiGateway_preserves_pre_registered_provider()
    {
        var expected = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The injected provider handled the call.",
            AiFinishReason.Stop);
        var services = new ServiceCollection();
        services.AddSingleton<IAiGatewayProvider>(new FixedAiGatewayProvider(expected));
        services.AddHiveAiGateway();

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.Same(expected, response);
    }

    [Fact]
    public async Task Default_provider_propagates_cancellation()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGateway();
        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gateway.CompleteAsync(Request(), cancellation.Token));
    }

    [Fact]
    public void AddHiveBootstrap_registers_gateway_without_starting_external_services()
    {
        var builder = Host.CreateApplicationBuilder(
            new[]
            {
                "--Hive:Node:Roles:0=api",
            });

        builder.AddHiveBootstrap();
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<IAiGateway>());
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");

    private sealed class FixedAiGatewayProvider(AiGatewayResponse response)
        : IAiGatewayProvider
    {
        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
