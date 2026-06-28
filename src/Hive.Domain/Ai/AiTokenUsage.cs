namespace Hive.Domain.Ai;

public sealed record AiTokenUsage
{
    public AiTokenUsage(
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        bool isEstimated = false)
    {
        InputTokens = RequireNonNegative(inputTokens, nameof(inputTokens));
        OutputTokens = RequireNonNegative(outputTokens, nameof(outputTokens));
        TotalTokens = RequireNonNegative(totalTokens, nameof(totalTokens));
        IsEstimated = isEstimated;
    }

    public int? InputTokens { get; }

    public int? OutputTokens { get; }

    public int? TotalTokens { get; }

    public bool IsEstimated { get; }

    private static int? RequireNonNegative(int? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Token count cannot be negative.");
        }

        return value;
    }
}
