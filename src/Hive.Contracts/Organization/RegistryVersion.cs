using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

public sealed record RegistryVersion
{
    public RegistryVersion(long version, string fingerprint)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "Registry version must be positive.");
        }

        Version = version;
        Fingerprint = OrganizationContractGuards.Fingerprint(
            fingerprint,
            nameof(fingerprint));
    }

    [JsonPropertyName("version")]
    public long Version { get; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; }
}
