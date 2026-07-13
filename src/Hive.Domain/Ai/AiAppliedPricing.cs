namespace Hive.Domain.Ai;

public sealed record AiAppliedPricing
{
    public AiAppliedPricing(
        string version,
        int tokenUnit,
        decimal inputPrice,
        decimal outputPrice,
        string currency)
    {
        if (tokenUnit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tokenUnit),
                tokenUnit,
                "Pricing token unit must be greater than zero.");
        }

        if (inputPrice < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputPrice),
                inputPrice,
                "Input price cannot be negative.");
        }

        if (outputPrice < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputPrice),
                outputPrice,
                "Output price cannot be negative.");
        }

        Version = AiContractGuards.RequireText(version, nameof(version));
        TokenUnit = tokenUnit;
        InputPrice = inputPrice;
        OutputPrice = outputPrice;
        Currency = AiContractGuards.RequireText(currency, nameof(currency));
    }

    public string Version { get; }

    public int TokenUnit { get; }

    public decimal InputPrice { get; }

    public decimal OutputPrice { get; }

    public string Currency { get; }
}
