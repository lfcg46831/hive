using Akka.Cluster;

namespace Hive.Actors.Sharding;

/// <summary>
/// Thrown when the <c>agents</c> workload cannot initialize Cluster Sharding because the
/// <c>ActorSystem</c> did not reach cluster <em>Up</em> within the configured timeout
/// (US-F0-06-T04d). Surfacing a dedicated, descriptive exception keeps the failed arranque
/// observable: the host's startup fails fast instead of starting sharding on a node that is not
/// yet a full cluster member.
/// </summary>
public sealed class ClusterStartupTimeoutException : Exception
{
    public ClusterStartupTimeoutException(string role, TimeSpan timeout, MemberStatus lastStatus)
        : base(
            $"Cluster Sharding for role '{role}' was not initialized because the ActorSystem did not "
            + $"reach cluster Up within {timeout} (last observed self-member status: {lastStatus}).")
    {
        Role = role;
        Timeout = timeout;
        LastStatus = lastStatus;
    }

    /// <summary>The node role whose workload gated on cluster <em>Up</em>.</summary>
    public string Role { get; }

    /// <summary>The timeout window that elapsed without the node reaching <em>Up</em>.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The last observed self-member status when the timeout elapsed.</summary>
    public MemberStatus LastStatus { get; }
}
