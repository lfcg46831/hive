namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The optional spend and rate limits of an AI occupant (§6.2 <c>occupant.ai.budget</c>): the daily
/// reactive and proactive caps in euro, the optional explicit global daily cap and the hourly call
/// limit. Every field is optional in the loaded model; relationships between them (for example the
/// global cap bounding the sum) are interpreted by later layers, not enforced here.
/// </summary>
public sealed record BudgetConfiguration
{
    /// <summary>Creates a budget from the optional caps, all of which may be omitted.</summary>
    public BudgetConfiguration(
        decimal? reactiveMaxEurPerDay = null,
        decimal? proactiveMaxEurPerDay = null,
        decimal? totalMaxEurPerDay = null,
        int? maxCallsPerHour = null)
    {
        ReactiveMaxEurPerDay = reactiveMaxEurPerDay;
        ProactiveMaxEurPerDay = proactiveMaxEurPerDay;
        TotalMaxEurPerDay = totalMaxEurPerDay;
        MaxCallsPerHour = maxCallsPerHour;
    }

    /// <summary>The daily cap, in euro, for reactive work.</summary>
    public decimal? ReactiveMaxEurPerDay { get; }

    /// <summary>The additive daily cap, in euro, for proactive work.</summary>
    public decimal? ProactiveMaxEurPerDay { get; }

    /// <summary>The optional explicit global daily cap, in euro, bounding the total.</summary>
    public decimal? TotalMaxEurPerDay { get; }

    /// <summary>The optional maximum number of calls per hour.</summary>
    public int? MaxCallsPerHour { get; }
}
