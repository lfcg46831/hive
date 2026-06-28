namespace Hive.Domain.Ai;

public enum AiGatewayErrorCode
{
    ConfigurationInvalid = 1,
    ProviderNotAuthorized = 2,
    ModelNotAuthorized = 3,
    ToolNotAuthorized = 4,
    BudgetInsufficient = 5,
    CredentialsMissing = 6,
    Timeout = 7,
    Canceled = 8,
    QuotaExceeded = 9,
    ProviderUnavailable = 10,
    ProviderRejected = 11,
    InvalidProviderResponse = 12,
    Unknown = 13,
}

public static class AiGatewayErrorCodeContract
{
    private static readonly AiProtocolEnumWireContract<AiGatewayErrorCode> Contract = new(
        (AiGatewayErrorCode.ConfigurationInvalid, "configuration-invalid"),
        (AiGatewayErrorCode.ProviderNotAuthorized, "provider-not-authorized"),
        (AiGatewayErrorCode.ModelNotAuthorized, "model-not-authorized"),
        (AiGatewayErrorCode.ToolNotAuthorized, "tool-not-authorized"),
        (AiGatewayErrorCode.BudgetInsufficient, "budget-insufficient"),
        (AiGatewayErrorCode.CredentialsMissing, "credentials-missing"),
        (AiGatewayErrorCode.Timeout, "timeout"),
        (AiGatewayErrorCode.Canceled, "canceled"),
        (AiGatewayErrorCode.QuotaExceeded, "quota-exceeded"),
        (AiGatewayErrorCode.ProviderUnavailable, "provider-unavailable"),
        (AiGatewayErrorCode.ProviderRejected, "provider-rejected"),
        (AiGatewayErrorCode.InvalidProviderResponse, "invalid-provider-response"),
        (AiGatewayErrorCode.Unknown, "unknown"));

    public static AiGatewayErrorCode RequireDefined(
        AiGatewayErrorCode value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiGatewayErrorCode value) => Contract.ToWireValue(value);

    public static AiGatewayErrorCode ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out AiGatewayErrorCode code) =>
        Contract.TryParseWireValue(value, out code);
}
