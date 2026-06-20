using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Configuration;

public static class HiveBootstrapExtensions
{
    public static IHostApplicationBuilder AddHiveBootstrap(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddHiveStructuredLogging();

        builder.Services.AddSingleton<IValidateOptions<HiveOptions>, HiveOptionsValidator>();
        builder.Services
            .AddOptions<HiveOptions>()
            .Bind(builder.Configuration.GetSection(HiveOptions.SectionName))
            .ValidateOnStart();

        builder.Services.AddSingleton<ActiveNodeRoles>();
        builder.Services.AddHostedService<RoleWorkloadHostedService>();

        builder.Services.AddHiveHealthChecks();

        return builder;
    }
}
