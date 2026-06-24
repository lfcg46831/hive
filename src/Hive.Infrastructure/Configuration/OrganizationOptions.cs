namespace Hive.Infrastructure.Configuration;

public sealed class OrganizationOptions
{
    public string RootPath { get; set; } = Path.Combine("config", "organizations");
}
