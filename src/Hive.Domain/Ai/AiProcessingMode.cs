namespace Hive.Domain.Ai;

public enum AiProcessingMode
{
    Interactive = 1,
    Batch = 2,
}

public static class AiProcessingModeContract
{
    private static readonly AiProtocolEnumWireContract<AiProcessingMode> Contract = new(
        (AiProcessingMode.Interactive, "interactive"),
        (AiProcessingMode.Batch, "batch"));

    public static AiProcessingMode RequireDefined(AiProcessingMode value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiProcessingMode value) => Contract.ToWireValue(value);

    public static AiProcessingMode ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out AiProcessingMode mode) =>
        Contract.TryParseWireValue(value, out mode);
}
