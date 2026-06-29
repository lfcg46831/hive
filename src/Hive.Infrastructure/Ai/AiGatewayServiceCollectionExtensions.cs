using Hive.Domain.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Infrastructure.Ai;

public static class AiGatewayServiceCollectionExtensions
{
    private const string AiGatewayProviderKey = "Hive:AiGateway:Provider";
    private const string StubProviderName = "stub";

    public static IServiceCollection AddHiveAiGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAiGatewayProvider, UnavailableAiGatewayProvider>();
        services.TryAddSingleton<IAiGateway, AiGateway>();

        return services;
    }

    public static IServiceCollection AddHiveAiGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHiveAiGateway();

        if (string.Equals(
            configuration[AiGatewayProviderKey],
            StubProviderName,
            StringComparison.OrdinalIgnoreCase))
        {
            services.AddHiveAiGatewayStub(options =>
                configuration
                    .GetSection(StubAiGatewayProviderOptions.SectionName)
                    .Bind(options));
        }

        return services;
    }

    public static IServiceCollection AddHiveAiGatewayStub(
        this IServiceCollection services,
        Action<StubAiGatewayProviderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHiveAiGateway();
        services.AddOptions<StubAiGatewayProviderOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.Replace(ServiceDescriptor.Singleton<
            IAiGatewayProvider,
            StubAiGatewayProvider>());

        return services;
    }
}
