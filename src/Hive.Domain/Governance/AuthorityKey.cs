using Hive.Domain.Identity;

namespace Hive.Domain.Governance;

/// <summary>Namespaced action-domain authority key, for example <c>delivery.bug-triage</c>.</summary>
public sealed record AuthorityKey
{
    private AuthorityKey(string value) => Value = value;

    public string Value { get; }

    public static AuthorityKey From(string value)
    {
        var structural = IdentityValue.RequireStructural(value, nameof(value));

        if (structural.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Authority key cannot contain whitespace.", nameof(value));
        }

        var segments = structural.Split('.');
        if (segments.Length < 2 || segments.Any(segment => segment.Length == 0))
        {
            throw new ArgumentException(
                "Authority key must be namespaced with at least two non-empty segments.",
                nameof(value));
        }

        return new AuthorityKey(structural);
    }

    public override string ToString() => Value;
}
