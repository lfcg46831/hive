using Hive.Domain.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Infrastructure.Connectors;

/// <summary>
/// Generic in-process activation seam implemented by connector plugin assemblies.
/// </summary>
public interface IConnectorPlugin
{
    ConnectorId Id { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
