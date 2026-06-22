using Hive.Domain.Identity;

namespace Hive.Domain.Governance;

/// <summary>
/// Immutable record of the approval policy/configuration actually applied when an approver was
/// resolved (US-F0-04-T07a). It captures the logical version label and the content hash of the
/// authority configuration in force at resolution time so routing and audit can persist exactly
/// what was interpreted, independently of later edits to the YAML/registry.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApprovalPolicyRef"/> is a stable logical selector and deliberately carries no
/// version. Reproducibility is achieved here: the resolver records the version/hash of the
/// configuration that produced a concrete approver, so a subsequent change to the policy never
/// retroactively alters the interpretation of an already-resolved request.
/// </para>
/// <para>
/// Both fields use the structural string rules shared by the identity value objects: they reject
/// <see langword="null"/>, empty, whitespace-only and outer-whitespace values, without trimming
/// or other silent normalization. Equality is ordinal and case-sensitive.
/// </para>
/// </remarks>
public sealed record ApprovalPolicyVersion
{
    private ApprovalPolicyVersion(string version, string hash)
    {
        Version = version;
        Hash = hash;
    }

    /// <summary>The logical version label of the applied authority configuration.</summary>
    public string Version { get; }

    /// <summary>The content hash of the applied authority configuration.</summary>
    public string Hash { get; }

    /// <summary>
    /// Creates a version stamp from the applied configuration's <paramref name="version"/> label
    /// and content <paramref name="hash"/>.
    /// </summary>
    public static ApprovalPolicyVersion Create(string version, string hash) =>
        new(
            IdentityValue.RequireStructural(version, nameof(version)),
            IdentityValue.RequireStructural(hash, nameof(hash)));

    public override string ToString() => $"{Version}@{Hash}";
}
