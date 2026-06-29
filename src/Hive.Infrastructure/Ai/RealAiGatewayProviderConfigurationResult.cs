using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Outcome of resolving the real provider configuration: either validated
/// <see cref="Settings"/> or a structured, wire-safe failure carrying an
/// <see cref="AiGatewayErrorCode"/> and a readable reason.
/// </summary>
public sealed class RealAiGatewayProviderConfigurationResult
{
    private RealAiGatewayProviderConfigurationResult(
        RealAiGatewayProviderSettings? settings,
        AiGatewayErrorCode? errorCode,
        string? failureReason)
    {
        Settings = settings;
        ErrorCode = errorCode;
        FailureReason = failureReason;
    }

    public bool IsSuccess => Settings is not null;

    public bool IsFailure => Settings is null;

    public RealAiGatewayProviderSettings? Settings { get; }

    public AiGatewayErrorCode? ErrorCode { get; }

    public string? FailureReason { get; }

    public static RealAiGatewayProviderConfigurationResult Success(
        RealAiGatewayProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new RealAiGatewayProviderConfigurationResult(settings, null, null);
    }

    public static RealAiGatewayProviderConfigurationResult Failure(
        AiGatewayErrorCode errorCode,
        string reason)
    {
        AiGatewayErrorCodeContract.RequireDefined(errorCode, nameof(errorCode));

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Failure reason cannot be empty or whitespace.",
                nameof(reason));
        }

        return new RealAiGatewayProviderConfigurationResult(null, errorCode, reason);
    }
}
