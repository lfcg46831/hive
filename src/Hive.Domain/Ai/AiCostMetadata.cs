namespace Hive.Domain.Ai;

public sealed record AiCostMetadata
{
    public AiCostMetadata(decimal amount, string currency, bool isEstimated)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                "Cost amount cannot be negative.");
        }

        Amount = amount;
        Currency = AiContractGuards.RequireText(currency, nameof(currency));
        IsEstimated = isEstimated;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public bool IsEstimated { get; }
}
