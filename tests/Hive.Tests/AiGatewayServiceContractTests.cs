using Hive.Domain.Ai;

namespace Hive.Tests;

public sealed class AiGatewayServiceContractTests
{
    [Fact]
    public void Gateway_port_lives_in_domain_and_uses_only_hive_contracts()
    {
        Assert.True(typeof(IAiGateway).IsInterface);
        Assert.Equal("Hive.Domain.Ai", typeof(IAiGateway).Namespace);
        Assert.Same(typeof(AiGatewayRequest).Assembly, typeof(IAiGateway).Assembly);

        var method = typeof(IAiGateway).GetMethod(nameof(IAiGateway.CompleteAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<AiGatewayResponse>), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Collection(
            parameters,
            request =>
            {
                Assert.Equal("request", request.Name);
                Assert.Equal(typeof(AiGatewayRequest), request.ParameterType);
            },
            cancellation =>
            {
                Assert.Equal("cancellationToken", cancellation.Name);
                Assert.Equal(typeof(CancellationToken), cancellation.ParameterType);
                Assert.True(cancellation.HasDefaultValue);
            });
    }
}
