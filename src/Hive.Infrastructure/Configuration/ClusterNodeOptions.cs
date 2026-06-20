namespace Hive.Infrastructure.Configuration;

/// <summary>
/// Akka.Cluster binding for this node. Defaults form a self-seeded single-node cluster so a
/// host boots out of the box; multi-node topologies override hostname/port and seed nodes via
/// configuration/env vars (US-F0-02).
/// </summary>
public sealed class ClusterNodeOptions
{
    public string Hostname { get; set; } = "localhost";

    public int Port { get; set; } = 8081;

    /// <summary>Seed nodes to join. When empty, the node seeds itself (single-node dev cluster).</summary>
    public string[] SeedNodes { get; set; } = [];
}
