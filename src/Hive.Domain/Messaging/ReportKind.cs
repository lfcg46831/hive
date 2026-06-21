namespace Hive.Domain.Messaging;

public enum ReportKind
{
    Progress = 1,
    Done = 2,
}

public static class ReportKindContract
{
    private static readonly ProtocolEnumWireContract<ReportKind> Contract = new(
        (ReportKind.Progress, "progress"),
        (ReportKind.Done, "done"));

    public static ReportKind RequireDefined(ReportKind value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(ReportKind value) => Contract.ToWireValue(value);

    public static ReportKind ParseWireValue(string value) => Contract.ParseWireValue(value);

    public static bool TryParseWireValue(string? value, out ReportKind kind) =>
        Contract.TryParseWireValue(value, out kind);
}
