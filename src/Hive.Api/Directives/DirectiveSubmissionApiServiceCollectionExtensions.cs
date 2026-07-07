using Hive.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Directives;

public static class DirectiveSubmissionApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveDirectiveSubmissionApi(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DirectiveRoutingValidator>();
        services.TryAddSingleton<IPositionCommandDispatcher, AkkaClusterShardingPositionCommandDispatcher>();
        services.TryAddSingleton<IDirectiveSubmissionSink, ShardedDirectiveSubmissionSink>();
        return services;
    }
}
