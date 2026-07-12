namespace Hive.Domain.Governance;

/// <summary>The fixed trust domain used when emitting retained-action grants.</summary>
public static class AuthorizationGrantAuthority
{
    public const string KeyValue = "governance.authorize-retained-action";

    public const string MessageSelector = "AuthorizationGrant";

    public static AuthorityKey Key { get; } = AuthorityKey.From(KeyValue);

    public static ActionDomainActionContract ActionContract { get; } =
        ActionDomainActionContract.ForOrganizationalMessage(MessageSelector);
}
