namespace Hive.Infrastructure.OccupantChannels;

/// <summary>
/// Operational SMTP settings. Credentials stay inside infrastructure configuration and never
/// cross the occupant-channel domain contract.
/// </summary>
public sealed class SmtpOccupantChannelOptions
{
    public const string SectionName = "Hive:OccupantChannels:Email:Smtp";

    public bool Enabled { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string Security { get; set; } = SmtpSecurityModeContract.StartTls;

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "HIVE";

    public string? ReplyToAddress { get; set; }

    public string SubjectPrefix { get; set; } = "[HIVE]";

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

internal static class SmtpSecurityModeContract
{
    public const string None = "none";
    public const string StartTls = "start-tls";
    public const string SslOnConnect = "ssl-on-connect";

    public static bool IsDefined(string? value) => value is None or StartTls or SslOnConnect;
}
