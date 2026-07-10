namespace Hive.Domain.Governance;

/// <summary>Closed outcome of resolving an <c>acting_under</c> declaration.</summary>
public enum ActingUnderDeclarationState
{
    Declared = 1,
    Missing = 2,
    Invalid = 3,
}

/// <summary>
/// Provider-neutral, immutable authority declaration supplied by an agent action.
/// Invalid input is deliberately not retained.
/// </summary>
public sealed record ActingUnderDeclaration
{
    public const string DeclaredCode = "acting-under-declared";
    public const string MissingCode = "acting-under-missing";
    public const string InvalidCode = "acting-under-invalid";

    private ActingUnderDeclaration(
        ActingUnderDeclarationState state,
        AuthorityKey? key)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown acting-under declaration state.");
        }

        if (state == ActingUnderDeclarationState.Declared && key is null)
        {
            throw new ArgumentNullException(nameof(key), "A declared acting-under value requires an authority key.");
        }

        if (state != ActingUnderDeclarationState.Declared && key is not null)
        {
            throw new ArgumentException(
                "Only a declared acting-under value can retain an authority key.",
                nameof(key));
        }

        State = state;
        Key = key;
        Code = state switch
        {
            ActingUnderDeclarationState.Declared => DeclaredCode,
            ActingUnderDeclarationState.Missing => MissingCode,
            ActingUnderDeclarationState.Invalid => InvalidCode,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }

    public ActingUnderDeclarationState State { get; }

    public AuthorityKey? Key { get; }

    public string Code { get; }

    public static ActingUnderDeclaration Declared(AuthorityKey key) =>
        new(ActingUnderDeclarationState.Declared, key ?? throw new ArgumentNullException(nameof(key)));

    public static ActingUnderDeclaration Missing() =>
        new(ActingUnderDeclarationState.Missing, key: null);

    public static ActingUnderDeclaration Invalid() =>
        new(ActingUnderDeclarationState.Invalid, key: null);

    /// <summary>
    /// Resolves a raw field value against the position's effective <c>can_decide</c> keys.
    /// Presence is separate from the value so that an absent field differs from a present null.
    /// </summary>
    public static ActingUnderDeclaration Resolve(
        bool fieldPresent,
        string? value,
        IEnumerable<AuthorityKey> allowedKeys)
    {
        ArgumentNullException.ThrowIfNull(allowedKeys);

        var allowedSnapshot = allowedKeys.ToArray();
        if (allowedSnapshot.Any(static key => key is null))
        {
            throw new ArgumentException("Allowed authority keys cannot contain null entries.", nameof(allowedKeys));
        }

        if (!fieldPresent)
        {
            return Missing();
        }

        AuthorityKey candidate;
        try
        {
            candidate = AuthorityKey.From(value!);
        }
        catch (ArgumentException)
        {
            return Invalid();
        }

        var allowedKey = allowedSnapshot.FirstOrDefault(
            key => string.Equals(key.Value, candidate.Value, StringComparison.Ordinal));

        return allowedKey is null
            ? Invalid()
            : Declared(allowedKey);
    }
}
