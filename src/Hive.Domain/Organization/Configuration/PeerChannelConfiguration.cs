using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Organization.Configuration;

public enum PeerChannelMessageType
{
    PeerRequest = 1,
    Memo = 2,
}

public static class PeerChannelMessageTypeContract
{
    private static readonly ProtocolEnumWireContract<PeerChannelMessageType> Contract = new(
        (PeerChannelMessageType.PeerRequest, "peer-request"),
        (PeerChannelMessageType.Memo, "memo"));

    public static PeerChannelMessageType RequireDefined(PeerChannelMessageType value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(PeerChannelMessageType value) => Contract.ToWireValue(value);

    public static bool TryParseWireValue(string? value, out PeerChannelMessageType result) =>
        Contract.TryParseWireValue(value, out result);
}

public enum PeerChannelRejectionAction
{
    None = 1,
    Escalate = 2,
}

public static class PeerChannelRejectionActionContract
{
    private static readonly ProtocolEnumWireContract<PeerChannelRejectionAction> Contract = new(
        (PeerChannelRejectionAction.None, "none"),
        (PeerChannelRejectionAction.Escalate, "escalate"));

    public static PeerChannelRejectionAction RequireDefined(PeerChannelRejectionAction value, string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(PeerChannelRejectionAction value) => Contract.ToWireValue(value);

    public static bool TryParseWireValue(string? value, out PeerChannelRejectionAction result) =>
        Contract.TryParseWireValue(value, out result);
}

/// <summary>A directional incoming contract owned by the unit exposing the channel.</summary>
public sealed record PeerChannelConfiguration
{
    public PeerChannelConfiguration(
        UnitId from,
        IReadOnlyList<PeerChannelMessageType> types,
        int maxOpenRequests,
        PeerChannelRejectionAction onRejection)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(types);
        var snapshot = types.ToImmutableArray();
        foreach (var type in snapshot)
        {
            PeerChannelMessageTypeContract.RequireDefined(type, nameof(types));
        }

        if (snapshot.IsEmpty || snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Channel types must be nonempty and unique.", nameof(types));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOpenRequests);
        From = from;
        Types = snapshot;
        MaxOpenRequests = maxOpenRequests;
        OnRejection = PeerChannelRejectionActionContract.RequireDefined(onRejection, nameof(onRejection));
    }

    public UnitId From { get; }
    public IReadOnlyList<PeerChannelMessageType> Types { get; }
    public int MaxOpenRequests { get; }
    public PeerChannelRejectionAction OnRejection { get; }
}
