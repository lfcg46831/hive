namespace Hive.Domain.Ai;

public sealed record AiCostLimits
{
    public AiCostLimits(
        decimal? reactiveMaxEurPerDay = null,
        decimal? proactiveMaxEurPerDay = null,
        decimal? totalMaxEurPerDay = null,
        int? maxCallsPerHour = null)
    {
        RequireNonNegative(reactiveMaxEurPerDay, nameof(reactiveMaxEurPerDay));
        RequireNonNegative(proactiveMaxEurPerDay, nameof(proactiveMaxEurPerDay));
        RequireNonNegative(totalMaxEurPerDay, nameof(totalMaxEurPerDay));
        if (maxCallsPerHour is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCallsPerHour),
                maxCallsPerHour,
                "Budget call limit must be greater than zero.");
        }

        ReactiveMaxEurPerDay = reactiveMaxEurPerDay;
        ProactiveMaxEurPerDay = proactiveMaxEurPerDay;
        TotalMaxEurPerDay = totalMaxEurPerDay;
        MaxCallsPerHour = maxCallsPerHour;
    }

    public decimal? ReactiveMaxEurPerDay { get; }

    public decimal? ProactiveMaxEurPerDay { get; }

    public decimal? TotalMaxEurPerDay { get; }

    public int? MaxCallsPerHour { get; }

    private static void RequireNonNegative(decimal? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Budget cost limit cannot be negative.");
        }
    }
}
