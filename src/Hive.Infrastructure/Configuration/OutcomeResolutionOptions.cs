using Hive.Domain.Outcomes;

namespace Hive.Infrastructure.Configuration;

public sealed class OutcomeResolutionOptions
{
    public static TimeSpan DefaultVerifierTimeout { get; } = TimeSpan.FromSeconds(15);

    public string Mode { get; set; } = OutcomeResolutionModeContract.Shadow;

    public TimeSpan VerifierTimeout { get; set; } = DefaultVerifierTimeout;
}
