namespace Hive.Infrastructure.Hosting;

/// <summary>
/// A unit of work that a node activates only when it declares the matching role.
/// Implementations are mechanism-specific (Cluster Sharding for <c>agents</c>, Cluster
/// Singletons for <c>connectors</c>, hosted endpoints for <c>api</c>, ...); this seam stays
/// agnostic so later stories plug real workloads without touching the host bootstrap.
/// </summary>
public interface IRoleWorkload
{
    /// <summary>Canonical node role this workload belongs to (see <c>NodeRoleNames</c>).</summary>
    string Role { get; }

    /// <summary>Activate the workload. Only invoked when <see cref="Role"/> is active on this node.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Tear the workload down. Only invoked for workloads that were started.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
