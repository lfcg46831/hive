namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryEntry<T>(
    T Value,
    string Fingerprint,
    DateTimeOffset UpdatedAt);
