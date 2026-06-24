namespace Hive.Infrastructure.Configuration;

public sealed class HiveOptions
{
    public const string SectionName = "Hive";

    public NodeOptions Node { get; set; } = new();

    public ClusterNodeOptions Cluster { get; set; } = new();

    public OrganizationOptions Organizations { get; set; } = new();
}
