namespace Hive.Domain.Ai;

public enum AiOutputConstraintMode
{
    JsonSchema = 1,
    JsonObject = 2,
    Text = 3,
}

public static class AiOutputConstraintModeContract
{
    private static readonly AiProtocolEnumWireContract<AiOutputConstraintMode> Contract = new(
        (AiOutputConstraintMode.JsonSchema, "json-schema"),
        (AiOutputConstraintMode.JsonObject, "json-object"),
        (AiOutputConstraintMode.Text, "text"));

    public static AiOutputConstraintMode RequireDefined(
        AiOutputConstraintMode value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiOutputConstraintMode value) =>
        Contract.ToWireValue(value);

    public static AiOutputConstraintMode ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiOutputConstraintMode mode) =>
        Contract.TryParseWireValue(value, out mode);
}
