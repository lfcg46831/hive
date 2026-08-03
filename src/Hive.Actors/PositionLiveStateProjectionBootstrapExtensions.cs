using Hive.Actors.Positions;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.ReadModels.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Hive.Actors;

public static class PositionLiveStateProjectionBootstrapExtensions
{
    public static IHostApplicationBuilder AddHivePositionLiveStateProjection(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<
            IPositionLiveStateProjectionFeed,
            PostgreSqlPositionLiveStateProjectionFeed>();
        builder.Services.TryAddSingleton<
            IPositionLiveStateProjectionJournal,
            AkkaPositionLiveStateProjectionJournal>();
        builder.Services.TryAddSingleton<PositionLiveStateProjectionWorker>();
        builder.Services.AddHostedService<PositionLiveStateProjectionHostedService>();
        return builder;
    }
}
