namespace Hive.Domain.Ai;

public enum AiFinishReason
{
    Stop = 1,
    Length = 2,
    ToolCalls = 3,
    ContentFiltered = 4,
    Unknown = 5,
}

public static class AiFinishReasonContract
{
    private static readonly AiProtocolEnumWireContract<AiFinishReason> Contract = new(
        (AiFinishReason.Stop, "stop"),
        (AiFinishReason.Length, "length"),
        (AiFinishReason.ToolCalls, "tool-calls"),
        (AiFinishReason.ContentFiltered, "content-filtered"),
        (AiFinishReason.Unknown, "unknown"));

    public static AiFinishReason RequireDefined(AiFinishReason value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiFinishReason value) => Contract.ToWireValue(value);

    public static AiFinishReason ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out AiFinishReason reason) =>
        Contract.TryParseWireValue(value, out reason);
}
