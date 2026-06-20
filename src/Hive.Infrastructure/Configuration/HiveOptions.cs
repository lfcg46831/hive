namespace Hive.Infrastructure.Configuration;

public sealed class HiveOptions
{
    public const string SectionName = "Hive";

    public NodeOptions Node { get; set; } = new();
}
