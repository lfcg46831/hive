namespace Hive.Domain.Identity;

public sealed record ActionFingerprint
{
    public const string AlgorithmPrefix = "sha256:";
    public const int DigestLength = 64;

    private ActionFingerprint(string value) => Value = value;

    public string Value { get; }

    public static ActionFingerprint From(string value)
    {
        var canonical = IdentityValue.RequireStructural(value, nameof(value));
        if (!canonical.StartsWith(AlgorithmPrefix, StringComparison.Ordinal)
            || canonical.Length != AlgorithmPrefix.Length + DigestLength
            || canonical.AsSpan(AlgorithmPrefix.Length).ContainsAnyExcept(
                "0123456789abcdef"))
        {
            throw new ArgumentException(
                $"Action fingerprint must use '{AlgorithmPrefix}' followed by {DigestLength} lowercase hexadecimal characters.",
                nameof(value));
        }

        return new(canonical);
    }

    public override string ToString() => Value;
}
