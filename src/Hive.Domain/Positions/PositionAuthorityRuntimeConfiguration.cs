using System.Collections.Immutable;

namespace Hive.Domain.Positions;

/// <summary>
/// Runtime authority rules projected for a position occupant (US-F0-06-T08a).
/// </summary>
public sealed record PositionAuthorityRuntimeConfiguration
{
    public PositionAuthorityRuntimeConfiguration(
        IEnumerable<string>? canDecide = null,
        IEnumerable<string>? mustEscalate = null,
        IEnumerable<string>? requiresHumanApproval = null)
    {
        CanDecide = ToValidatedArray(canDecide, nameof(canDecide));
        MustEscalate = ToValidatedArray(mustEscalate, nameof(mustEscalate));
        RequiresHumanApproval = ToValidatedArray(requiresHumanApproval, nameof(requiresHumanApproval));
    }

    /// <summary>Action labels the occupant may decide autonomously.</summary>
    public ImmutableArray<string> CanDecide { get; }

    /// <summary>Action labels the occupant must escalate.</summary>
    public ImmutableArray<string> MustEscalate { get; }

    /// <summary>Action labels that require human approval.</summary>
    public ImmutableArray<string> RequiresHumanApproval { get; }

    private static ImmutableArray<string> ToValidatedArray(IEnumerable<string>? source, string parameterName)
    {
        if (source is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in source)
        {
            builder.Add(CommandText.RequireContent(item, parameterName));
        }

        return builder.ToImmutable();
    }
}
