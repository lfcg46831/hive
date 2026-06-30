namespace Hive.Domain.Ai;

public enum AiGatewayCallResult
{
    Succeeded = 1,
    Failed = 2,
}

public static class AiGatewayCallResultContract
{
    private static readonly AiProtocolEnumWireContract<AiGatewayCallResult> Contract = new(
        (AiGatewayCallResult.Succeeded, "succeeded"),
        (AiGatewayCallResult.Failed, "failed"));

    public static AiGatewayCallResult RequireDefined(
        AiGatewayCallResult value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiGatewayCallResult value) =>
        Contract.ToWireValue(value);

    public static AiGatewayCallResult ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiGatewayCallResult result) =>
        Contract.TryParseWireValue(value, out result);
}
