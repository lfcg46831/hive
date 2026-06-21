namespace Hive.Domain.Messaging;

public enum RejectionReason
{
    InvalidContract = 1,
    UnsupportedSchemaVersion = 2,
    InvalidRoute = 3,
    Unauthorized = 4,
    Duplicate = 5,
    Expired = 6,
}

public static class RejectionReasonContract
{
    private static readonly ProtocolEnumWireContract<RejectionReason> Contract = new(
        (RejectionReason.InvalidContract, "invalid-contract"),
        (RejectionReason.UnsupportedSchemaVersion, "unsupported-schema-version"),
        (RejectionReason.InvalidRoute, "invalid-route"),
        (RejectionReason.Unauthorized, "unauthorized"),
        (RejectionReason.Duplicate, "duplicate"),
        (RejectionReason.Expired, "expired"));

    public static RejectionReason RequireDefined(RejectionReason value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(RejectionReason value) => Contract.ToWireValue(value);

    public static RejectionReason ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out RejectionReason reason) =>
        Contract.TryParseWireValue(value, out reason);
}
