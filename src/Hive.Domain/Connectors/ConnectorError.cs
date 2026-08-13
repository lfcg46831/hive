namespace Hive.Domain.Connectors;

public enum ConnectorErrorCode
{
    InvalidInput = 1,
    ConfigurationInvalid = 2,
    CapabilityUnavailable = 3,
    ScopeDenied = 4,
    AuthenticationFailed = 5,
    RateLimited = 6,
    Timeout = 7,
    Canceled = 8,
    ExternalUnavailable = 9,
    ExternalRejected = 10,
    MappingFailed = 11,
    Unknown = 12,
}

/// <summary>
/// Provider- and transport-neutral connector failure. It deliberately excludes raw external error
/// text and credentials; callers receive only a stable code, retry classification and safe path.
/// </summary>
public sealed record ConnectorError
{
    public ConnectorError(
        ConnectorErrorCode code,
        bool isRetryable,
        string? path = null)
    {
        Code = ConnectorErrorCodeContract.RequireDefined(code, nameof(code));
        IsRetryable = isRetryable;
        Path = path is null ? null : ConnectorContractGuards.RequirePath(path, nameof(path));
    }

    public ConnectorErrorCode Code { get; }

    public bool IsRetryable { get; }

    public string? Path { get; }
}

public static class ConnectorErrorCodeContract
{
    public static ConnectorErrorCode RequireDefined(
        ConnectorErrorCode value,
        string parameterName) => value switch
        {
            ConnectorErrorCode.InvalidInput or
            ConnectorErrorCode.ConfigurationInvalid or
            ConnectorErrorCode.CapabilityUnavailable or
            ConnectorErrorCode.ScopeDenied or
            ConnectorErrorCode.AuthenticationFailed or
            ConnectorErrorCode.RateLimited or
            ConnectorErrorCode.Timeout or
            ConnectorErrorCode.Canceled or
            ConnectorErrorCode.ExternalUnavailable or
            ConnectorErrorCode.ExternalRejected or
            ConnectorErrorCode.MappingFailed or
            ConnectorErrorCode.Unknown => value,
            _ => throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Connector error code is undefined."),
        };

    public static string ToWireValue(ConnectorErrorCode value) =>
        RequireDefined(value, nameof(value)) switch
        {
            ConnectorErrorCode.InvalidInput => "invalid-input",
            ConnectorErrorCode.ConfigurationInvalid => "configuration-invalid",
            ConnectorErrorCode.CapabilityUnavailable => "capability-unavailable",
            ConnectorErrorCode.ScopeDenied => "scope-denied",
            ConnectorErrorCode.AuthenticationFailed => "authentication-failed",
            ConnectorErrorCode.RateLimited => "rate-limited",
            ConnectorErrorCode.Timeout => "timeout",
            ConnectorErrorCode.Canceled => "canceled",
            ConnectorErrorCode.ExternalUnavailable => "external-unavailable",
            ConnectorErrorCode.ExternalRejected => "external-rejected",
            ConnectorErrorCode.MappingFailed => "mapping-failed",
            ConnectorErrorCode.Unknown => "unknown",
            _ => throw new InvalidOperationException("Validated connector error code is not mapped."),
        };

    public static bool TryParseWireValue(string? value, out ConnectorErrorCode result)
    {
        result = value switch
        {
            "invalid-input" => ConnectorErrorCode.InvalidInput,
            "configuration-invalid" => ConnectorErrorCode.ConfigurationInvalid,
            "capability-unavailable" => ConnectorErrorCode.CapabilityUnavailable,
            "scope-denied" => ConnectorErrorCode.ScopeDenied,
            "authentication-failed" => ConnectorErrorCode.AuthenticationFailed,
            "rate-limited" => ConnectorErrorCode.RateLimited,
            "timeout" => ConnectorErrorCode.Timeout,
            "canceled" => ConnectorErrorCode.Canceled,
            "external-unavailable" => ConnectorErrorCode.ExternalUnavailable,
            "external-rejected" => ConnectorErrorCode.ExternalRejected,
            "mapping-failed" => ConnectorErrorCode.MappingFailed,
            "unknown" => ConnectorErrorCode.Unknown,
            _ => default,
        };

        return result != default;
    }
}
