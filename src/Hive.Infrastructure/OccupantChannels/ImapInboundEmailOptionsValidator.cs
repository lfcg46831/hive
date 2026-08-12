using System.Text.RegularExpressions;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed partial class ImapInboundEmailOptionsValidator(
    ActiveNodeRoles activeRoles,
    IConfiguration configuration) : IValidateOptions<ImapInboundEmailOptions>
{
    private const string Prefix = ImapInboundEmailOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, ImapInboundEmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!activeRoles.Contains(NodeRoleNames.Connectors) || !options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        Required(options.Host, $"{Prefix}:Host", failures);
        Required(options.Username, $"{Prefix}:Username", failures);
        Required(options.Password, $"{Prefix}:Password", failures);

        if (!SourceIdPattern().IsMatch(options.SourceId ?? string.Empty))
        {
            failures.Add(
                $"{Prefix}:SourceId must be 1-128 lowercase letters, digits, '.', '_' or '-', and start with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(options.Mailbox)
            || options.Mailbox.Length > 255
            || !string.Equals(options.Mailbox, options.Mailbox.Trim(), StringComparison.Ordinal)
            || options.Mailbox.Any(char.IsControl))
        {
            failures.Add(
                $"{Prefix}:Mailbox must be a trimmed, non-empty mailbox path of at most 255 characters without control characters.");
        }

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"{Prefix}:Port must be between 1 and 65535.");
        }

        if (!ImapSecurityModeContract.IsDefined(options.Security))
        {
            failures.Add(
                $"{Prefix}:Security must be 'none', 'start-tls', or 'ssl-on-connect'.");
        }

        if (options.PollInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add($"{Prefix}:PollInterval must be at least one second.");
        }

        if (options.BatchSize is < 1 or > 500)
        {
            failures.Add($"{Prefix}:BatchSize must be between 1 and 500.");
        }

        if (options.MaxMessageBytes is < 1024 or > 50 * 1024 * 1024)
        {
            failures.Add(
                $"{Prefix}:MaxMessageBytes must be between 1024 and 52428800 bytes.");
        }

        Positive(options.OperationTimeout, $"{Prefix}:OperationTimeout", failures);
        Positive(options.ClusterUpTimeout, $"{Prefix}:ClusterUpTimeout", failures);

        if (string.IsNullOrWhiteSpace(
                configuration.GetConnectionString(ConnectionStringNames.PostgreSql)))
        {
            failures.Add(
                $"ConnectionStrings:{ConnectionStringNames.PostgreSql} is required when IMAP ingestion is enabled.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Required(string? value, string path, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{path} is required when IMAP ingestion is enabled.");
        }
    }

    private static void Positive(TimeSpan value, string path, ICollection<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{path} must be greater than zero.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdPattern();
}
