namespace Hive.Domain.Connectors;

/// <summary>
/// Public, external-system-neutral connector seam. Transport polling, credentials, checkpoints and
/// action execution live outside the domain contract.
/// </summary>
public interface IConnector
{
    ConnectorId Id { get; }

    ConnectorVersion Version { get; }

    ConnectorCapability Capabilities { get; }

    ConnectorConfigurationSchema ConfigurationSchema { get; }

    IConnectorInboundMessageMapper? InboundMessageMapper { get; }

    IConnectorOutboundMessageMapper? OutboundMessageMapper { get; }

    IReadOnlyList<ConnectorOutboundAction> OutboundActions { get; }
}
