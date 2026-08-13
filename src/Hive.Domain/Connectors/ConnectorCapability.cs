namespace Hive.Domain.Connectors;

[Flags]
public enum ConnectorCapability
{
    None = 0,
    InboundMessages = 1,
    OutboundMessages = 2,
    OutboundActions = 4,
}

public static class ConnectorCapabilityContract
{
    private const ConnectorCapability All =
        ConnectorCapability.InboundMessages |
        ConnectorCapability.OutboundMessages |
        ConnectorCapability.OutboundActions;

    public static ConnectorCapability RequireSupported(
        ConnectorCapability value,
        string parameterName)
    {
        if (value == ConnectorCapability.None || (value & ~All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Connector capabilities must contain at least one supported capability.");
        }

        return value;
    }

    public static bool Contains(
        this ConnectorCapability capabilities,
        ConnectorCapability capability) =>
        (capabilities & capability) == capability;
}
