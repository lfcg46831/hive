namespace Hive.Contracts.Audit;

/// <summary>
/// Stable identity and version of the public directive audit/export wire contract.
/// </summary>
public static class AuditExportContract
{
    public const string Name = "hive.directive-audit-export";

    public const int Version = 1;

    public const string ResultMediaType = "application/vnd.hive.org-message+json";

    public const string AcceptedObservationMediaType =
        "application/vnd.hive.accepted-observation+json";
}

/// <summary>
/// Hard bounds applied to every v1 audit/export response.
/// </summary>
public static class AuditExportContractLimits
{
    public const int MaxEventsPerPage = 100;

    public const int MaxAttributesPerEvent = 64;

    public const int MaxAttributeKeyLength = 128;

    public const int MaxAttributeValueLength = 2_048;

    public const int MaxAttributePayloadBytes = 32 * 1_024;

    public const int MaxResultContentBytes = 64 * 1_024;

    public const int MaxAcceptedObservationContentBytes = 4 * 1_024;
}
