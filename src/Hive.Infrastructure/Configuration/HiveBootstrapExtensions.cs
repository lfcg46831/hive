using Hive.Domain.Positions;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Logging;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.Persistence.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.AddHiveAiGateway();
        builder.Services.TryAddSingleton<IPositionConfigurationProvider>(serviceProvider =>
        {
            var connectionString = serviceProvider
                .GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringNames.PostgreSql);

            return string.IsNullOrWhiteSpace(connectionString)
                ? new UnavailablePositionConfigurationProvider(ConnectionStringNames.PostgreSql)
                : new PostgreSqlPositionConfigurationProvider(connectionString);
        });
        builder.Services.AddHostedService<PostgreSqlOrganizationRegistryMigrationHostedService>();
        builder.Services.AddHostedService<PostgreSqlOrganizationRegistryImportHostedService>();
        builder.Services.AddHostedService<PostgreSqlPositionPersistenceMigrationHostedService>();
        builder.Services.AddHostedService<RoleWorkloadHostedService>();

        builder.Services.AddHiveHealthChecks();

        return builder;
    }
}
