namespace Hive.Infrastructure.OccupantChannels;

/// <summary>Operational signing settings; the key never crosses the infrastructure boundary.</summary>
public sealed class OccupantChannelCorrelationTokenOptions
{
    public const string SectionName = "Hive:OccupantChannels:CorrelationTokens";

    /// <summary>Base64-encoded HMAC key containing at least 256 bits of entropy.</summary>
    public string? SigningKey { get; set; }

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);
}
