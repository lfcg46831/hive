namespace Hive.Domain.Ai;

public enum AiGatewayErrorReason
{
    CircuitOpen = 1,
    FallbackExhausted = 2,
}

public static class AiGatewayErrorReasonContract
{
    private static readonly AiProtocolEnumWireContract<AiGatewayErrorReason> Contract = new(
        (AiGatewayErrorReason.CircuitOpen, "circuit-open"),
        (AiGatewayErrorReason.FallbackExhausted, "fallback-exhausted"));

    public static AiGatewayErrorReason RequireDefined(
        AiGatewayErrorReason value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiGatewayErrorReason value) =>
        Contract.ToWireValue(value);

    public static AiGatewayErrorReason ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiGatewayErrorReason reason) =>
        Contract.TryParseWireValue(value, out reason);
}
