namespace Hive.Domain.Messaging;

public enum MessageState
{
    Received = 1,
    Accepted = 2,
    Processing = 3,
    Completed = 4,
    Rejected = 5,
    Failed = 6,
}

public static class MessageStateContract
{
    private static readonly ProtocolEnumWireContract<MessageState> Contract = new(
        (MessageState.Received, "received"),
        (MessageState.Accepted, "accepted"),
        (MessageState.Processing, "processing"),
        (MessageState.Completed, "completed"),
        (MessageState.Rejected, "rejected"),
        (MessageState.Failed, "failed"));

    private static readonly HashSet<(MessageState From, MessageState To)> AllowedTransitions =
    [
        (MessageState.Received, MessageState.Rejected),
        (MessageState.Received, MessageState.Accepted),
        (MessageState.Accepted, MessageState.Processing),
        (MessageState.Processing, MessageState.Completed),
        (MessageState.Processing, MessageState.Failed),
    ];

    public static MessageState RequireDefined(MessageState value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(MessageState value) => Contract.ToWireValue(value);

    public static MessageState ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out MessageState state) =>
        Contract.TryParseWireValue(value, out state);

    public static bool CanTransition(MessageState from, MessageState to)
    {
        Contract.RequireDefined(from, nameof(from));
        Contract.RequireDefined(to, nameof(to));

        return AllowedTransitions.Contains((from, to));
    }
}
