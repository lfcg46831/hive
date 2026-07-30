using Hive.Domain.Auditing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Api.Auditing;

public static class DirectiveAuditExportApiServiceCollectionExtensions
{
    public static IServiceCollection AddHiveDirectiveAuditExportApi(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IDirectiveAuditExportReader>(
            _ => NoopDirectiveAuditExportStore.Instance);
        return services;
    }
}
