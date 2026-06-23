namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// One link of the AI fallback chain (§6.2 <c>occupant.ai.fallback[]</c>): the alternative
/// <see cref="Provider"/> and <see cref="Model"/> tried, in order, when the primary provider fails.
/// </summary>
public sealed record AiFallbackConfiguration
{
    /// <summary>Creates a fallback to <paramref name="provider"/>/<paramref name="model"/>.</summary>
    public AiFallbackConfiguration(string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        Provider = provider;
        Model = model;
    }

    /// <summary>The fallback provider registered in the AI gateway.</summary>
    public string Provider { get; }

    /// <summary>The fallback model identifier.</summary>
    public string Model { get; }
}
