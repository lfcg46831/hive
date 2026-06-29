using Hive.Domain.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Infrastructure.Ai;

public static class AiGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddHiveAiGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAiGatewayProvider, UnavailableAiGatewayProvider>();
        services.TryAddSingleton<IAiGateway, AiGateway>();

        return services;
    }
}
