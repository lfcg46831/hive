namespace Hive.Actors.OccupantChannels;

internal static class ImapInboundEmailSingletonIdentity
{
    public const string SingletonManagerName = "occupant-email-imap-singleton-manager";
    public const string SingletonName = "source";
    public const string ProxyName = "occupant-email-imap";
    public const string SingletonManagerPath = "/user/" + SingletonManagerName;
}
