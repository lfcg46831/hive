namespace Hive.Infrastructure.OccupantChannels;

/// <summary>
/// Operational settings for the transport-only IMAP source. Personal endpoints are never part of
/// this configuration: this is the shared HIVE reply mailbox, not an occupant binding.
/// </summary>
public sealed class ImapInboundEmailOptions
{
    public const string SectionName = "Hive:OccupantChannels:Email:Imap";

    public bool Enabled { get; set; }

    public string SourceId { get; set; } = "occupant-replies";

    public string? Host { get; set; }

    public int Port { get; set; } = 993;

    public string Security { get; set; } = ImapSecurityModeContract.SslOnConnect;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string Mailbox { get; set; } = "INBOX";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int BatchSize { get; set; } = 50;

    public int MaxMessageBytes { get; set; } = 10 * 1024 * 1024;

    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ClusterUpTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

internal static class ImapSecurityModeContract
{
    public const string None = "none";
    public const string StartTls = "start-tls";
    public const string SslOnConnect = "ssl-on-connect";

    public static bool IsDefined(string? value) => value is None or StartTls or SslOnConnect;
}
