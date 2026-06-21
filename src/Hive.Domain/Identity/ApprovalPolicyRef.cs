namespace Hive.Domain.Identity;

public sealed record ApprovalPolicyRef
{
    private ApprovalPolicyRef(string value) => Value = value;

    public string Value { get; }

    public static ApprovalPolicyRef From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
