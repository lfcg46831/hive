namespace Hive.Domain.Ai;

public sealed record AiModelParameters
{
    public AiModelParameters(
        decimal? temperature = null,
        int? maxOutputTokens = null)
    {
        if (temperature is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperature),
                temperature,
                "Temperature must be between 0 and 2.");
        }

        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                maxOutputTokens,
                "Max output tokens must be greater than zero.");
        }

        Temperature = temperature;
        MaxOutputTokens = maxOutputTokens;
    }

    public static AiModelParameters Default { get; } = new();

    public decimal? Temperature { get; }

    public int? MaxOutputTokens { get; }
}
