using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

public sealed record AuditExportUsage
{
    public AuditExportUsage(
        int inputTokens,
        int outputTokens,
        int totalTokens,
        bool estimated)
    {
        if (inputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens));
        }

        if (outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        }

        if (totalTokens < inputTokens || totalTokens < outputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalTokens),
                totalTokens,
                "Total tokens cannot be smaller than either component.");
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
        Estimated = estimated;
    }

    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; }

    [JsonPropertyName("estimated")]
    public bool Estimated { get; }
}
