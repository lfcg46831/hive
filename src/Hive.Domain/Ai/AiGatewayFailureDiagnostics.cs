namespace Hive.Domain.Ai;

public sealed record AiGatewayFailureDiagnostics
{
    public AiGatewayFailureDiagnostics(
        AiFinishReason? finishReason = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        AiAppliedPricing? appliedPricing = null,
        int? providerStatusCode = null)
    {
        if (finishReason is { } reason)
        {
            AiFinishReasonContract.RequireDefined(reason, nameof(finishReason));
        }

        if (providerStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerStatusCode),
                providerStatusCode,
                "Provider status code must be between 100 and 599.");
        }

        if (appliedPricing is not null &&
            (cost is null ||
             !cost.IsEstimated ||
             !string.Equals(
                 cost.Currency,
                 appliedPricing.Currency,
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Applied pricing requires estimated cost metadata in the same currency.",
                nameof(appliedPricing));
        }

        FinishReason = finishReason;
        Usage = usage;
        Cost = cost;
        AppliedPricing = appliedPricing;
        ProviderStatusCode = providerStatusCode;
    }

    public AiFinishReason? FinishReason { get; }

    public AiTokenUsage? Usage { get; }

    public AiCostMetadata? Cost { get; }

    public AiAppliedPricing? AppliedPricing { get; }

    public int? ProviderStatusCode { get; }
}
