namespace Hive.Domain.Ai;

public enum AiCostStatus
{
    ProviderReported = 1,
    Estimated = 2,
    Unavailable = 3,
}

public static class AiCostStatusContract
{
    private static readonly AiProtocolEnumWireContract<AiCostStatus> Contract = new(
        (AiCostStatus.ProviderReported, "provider-reported"),
        (AiCostStatus.Estimated, "estimated"),
        (AiCostStatus.Unavailable, "cost-unavailable"));

    public static AiCostStatus RequireDefined(
        AiCostStatus value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiCostStatus value) =>
        Contract.ToWireValue(value);

    public static AiCostStatus ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiCostStatus status) =>
        Contract.TryParseWireValue(value, out status);
}
