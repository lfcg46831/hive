using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class SmtpOccupantChannelOptionsValidator(ActiveNodeRoles activeRoles)
    : IValidateOptions<SmtpOccupantChannelOptions>
{
    private const string Prefix = SmtpOccupantChannelOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, SmtpOccupantChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!activeRoles.Contains(NodeRoleNames.Connectors) || !options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        Required(options.Host, $"{Prefix}:Host", failures);
        Address(options.FromAddress, $"{Prefix}:FromAddress", required: true, failures);
        Address(options.ReplyToAddress, $"{Prefix}:ReplyToAddress", required: false, failures);

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"{Prefix}:Port must be between 1 and 65535.");
        }

        if (!SmtpSecurityModeContract.IsDefined(options.Security))
        {
            failures.Add(
                $"{Prefix}:Security must be 'none', 'start-tls', or 'ssl-on-connect'.");
        }

        if (string.IsNullOrWhiteSpace(options.Username) !=
            string.IsNullOrWhiteSpace(options.Password))
        {
            failures.Add($"{Prefix}:Username and {Prefix}:Password must be supplied together.");
        }

        Header(options.FromName, $"{Prefix}:FromName", failures);
        Header(options.SubjectPrefix, $"{Prefix}:SubjectPrefix", failures);

        if (options.MaxAttempts is < 1 or > 10)
        {
            failures.Add($"{Prefix}:MaxAttempts must be between 1 and 10.");
        }

        Positive(options.InitialBackoff, $"{Prefix}:InitialBackoff", failures);
        Positive(options.MaxBackoff, $"{Prefix}:MaxBackoff", failures);
        Positive(options.AttemptTimeout, $"{Prefix}:AttemptTimeout", failures);

        if (options.InitialBackoff > options.MaxBackoff)
        {
            failures.Add($"{Prefix}:InitialBackoff must not exceed {Prefix}:MaxBackoff.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Required(string? value, string path, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{path} is required when SMTP delivery is enabled.");
        }
    }

    private static void Address(
        string? value,
        string path,
        bool required,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                failures.Add($"{path} is required when SMTP delivery is enabled.");
            }

            return;
        }

        if (!MailboxAddress.TryParse(value, out var address) ||
            !string.Equals(value, address.Address, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{path} must contain one normalized mailbox address without a display name.");
        }
    }

    private static void Header(string? value, string path, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{path} must not be empty.");
        }
        else if (value.Contains('\r', StringComparison.Ordinal) ||
                 value.Contains('\n', StringComparison.Ordinal))
        {
            failures.Add($"{path} must not contain line breaks.");
        }
    }

    private static void Positive(TimeSpan value, string path, ICollection<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{path} must be greater than zero.");
        }
    }
}
