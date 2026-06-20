using Microsoft.Extensions.DependencyInjection;

namespace Hive.Infrastructure.Hosting;

public static class RoleWorkloadServiceCollectionExtensions
{
    /// <summary>
    /// Registers a role-scoped workload. The host starts it only when the node declares the
    /// workload's role. Later stories use this to plug real workloads into the existing seam.
    /// </summary>
    public static IServiceCollection AddRoleWorkload<TWorkload>(this IServiceCollection services)
        where TWorkload : class, IRoleWorkload
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRoleWorkload, TWorkload>();
        return services;
    }
}
