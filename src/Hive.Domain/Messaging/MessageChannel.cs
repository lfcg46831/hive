namespace Hive.Domain.Messaging;

public enum MessageChannel
{
    Vertical = 1,
    Horizontal = 2,
    Governance = 3,
    System = 4,
}

public static class MessageChannelContract
{
    private static readonly ProtocolEnumWireContract<MessageChannel> Contract = new(
        (MessageChannel.Vertical, "vertical"),
        (MessageChannel.Horizontal, "horizontal"),
        (MessageChannel.Governance, "governance"),
        (MessageChannel.System, "system"));

    public static MessageChannel RequireDefined(MessageChannel value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(MessageChannel value) => Contract.ToWireValue(value);

    public static MessageChannel ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out MessageChannel channel) =>
        Contract.TryParseWireValue(value, out channel);
}
