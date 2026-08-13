using Hive.Domain.Connectors;

namespace Hive.Infrastructure.Connectors;

public sealed record ConnectorPluginDescriptor(
    ConnectorId Id,
    string AssemblyName,
    string TypeName);

/// <summary>Immutable description of the connector plugins activated by the host.</summary>
public sealed class ConnectorPluginCatalog
{
    internal ConnectorPluginCatalog(IEnumerable<ConnectorPluginDescriptor> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        Plugins = Array.AsReadOnly(plugins.ToArray());
    }

    public IReadOnlyList<ConnectorPluginDescriptor> Plugins { get; }
}
