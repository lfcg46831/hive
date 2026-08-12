using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.OccupantChannels.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal static class OccupantChannelCorrelationTokenServiceCollectionExtensions
{
    public static IServiceCollection AddHiveOccupantChannelCorrelationTokens(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<OccupantChannelCorrelationTokenOptions>,
            OccupantChannelCorrelationTokenOptionsValidator>();
        services
            .AddOptions<OccupantChannelCorrelationTokenOptions>()
            .Bind(configuration.GetSection(OccupantChannelCorrelationTokenOptions.SectionName))
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOccupantChannelDecisionTokenUseStore>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);
            return string.IsNullOrWhiteSpace(connectionString)
                ? UnavailableOccupantChannelDecisionTokenUseStore.Instance
                : new PostgreSqlOccupantChannelDecisionTokenUseStore(connectionString);
        });

        var signingKey = configuration[
            $"{OccupantChannelCorrelationTokenOptions.SectionName}:SigningKey"];
        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            services.TryAddSingleton<
                IOccupantChannelCorrelationTokenService,
                HmacOccupantChannelCorrelationTokenService>();
            services.TryAddSingleton<
                IOccupantChannelDeliveryRequestFactory,
                SignedOccupantChannelDeliveryRequestFactory>();
        }

        services.AddHostedService<PostgreSqlOccupantChannelTokenMigrationHostedService>();
        return services;
    }
}
