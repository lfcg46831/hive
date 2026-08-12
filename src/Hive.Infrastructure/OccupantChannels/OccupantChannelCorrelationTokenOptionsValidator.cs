using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class OccupantChannelCorrelationTokenOptionsValidator(
    ActiveNodeRoles activeRoles,
    IConfiguration configuration)
    : IValidateOptions<OccupantChannelCorrelationTokenOptions>
{
    private const int MinimumKeyBytes = 32;
    private const string Prefix = OccupantChannelCorrelationTokenOptions.SectionName;

    public ValidateOptionsResult Validate(
        string? name,
        OccupantChannelCorrelationTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var signingKeyRequired = activeRoles.Contains(NodeRoleNames.Connectors) &&
            configuration.GetValue<bool>(
                $"{SmtpOccupantChannelOptions.SectionName}:Enabled");

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            if (signingKeyRequired)
            {
                failures.Add(
                    $"{Prefix}:SigningKey is required when SMTP occupant delivery is enabled on a connectors node.");
            }
        }
        else if (!TryDecodeKey(options.SigningKey, out var keyLength) ||
                 keyLength < MinimumKeyBytes)
        {
            failures.Add(
                $"{Prefix}:SigningKey must be valid Base64 encoding at least {MinimumKeyBytes} bytes.");
        }

        if (options.Lifetime < TimeSpan.FromSeconds(1) ||
            options.Lifetime > TimeSpan.FromDays(30))
        {
            failures.Add($"{Prefix}:Lifetime must be at least one second and at most 30 days.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryDecodeKey(string value, out int keyLength)
    {
        keyLength = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            keyLength = bytes.Length;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
