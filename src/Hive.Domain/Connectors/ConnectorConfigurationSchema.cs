using System.Collections.Immutable;
using System.Text.Json;

namespace Hive.Domain.Connectors;

[Flags]
public enum ConnectorScopeDirection
{
    None = 0,
    Inbound = 1,
    Outbound = 2,
    Both = Inbound | Outbound,
}

/// <summary>One scope dimension enforced at the connector boundary.</summary>
public sealed record ConnectorScopeDefinition
{
    public ConnectorScopeDefinition(
        string name,
        ConnectorScopeDirection direction,
        string configurationPath,
        string description)
    {
        Name = ConnectorContractGuards.RequireToken(name, nameof(name));
        Direction = ConnectorContractGuards.RequireScopeDirection(direction, nameof(direction));
        ConfigurationPath = ConnectorContractGuards.RequireConfigurationPath(
            configurationPath,
            nameof(configurationPath));
        Description = ConnectorContractGuards.RequireText(description, nameof(description));
    }

    public string Name { get; }

    public ConnectorScopeDirection Direction { get; }

    public string ConfigurationPath { get; }

    public string Description { get; }
}

/// <summary>
/// Versioned, immutable JSON configuration schema plus the scope-bearing paths whose values must be
/// enforced for inbound and/or outbound operations.
/// </summary>
public sealed record ConnectorConfigurationSchema
{
    public ConnectorConfigurationSchema(
        int version,
        JsonElement schema,
        IReadOnlyList<ConnectorScopeDefinition>? scopes = null)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                version,
                "Connector configuration schema version must be positive.");
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Connector configuration schema must be a JSON object.",
                nameof(schema));
        }

        var scopeSnapshot = ConnectorContractGuards.Snapshot(scopes, nameof(scopes));
        var duplicateName = scopeSnapshot
            .GroupBy(scope => scope.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new ArgumentException(
                $"Connector scope '{duplicateName.Key}' is declared more than once.",
                nameof(scopes));
        }

        var duplicatePath = scopeSnapshot
            .GroupBy(scope => scope.ConfigurationPath, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null)
        {
            throw new ArgumentException(
                $"Connector configuration path '{duplicatePath.Key}' is scoped more than once.",
                nameof(scopes));
        }

        Version = version;
        Schema = schema.Clone();
        Scopes = scopeSnapshot;
    }

    public int Version { get; }

    public JsonElement Schema { get; }

    public IReadOnlyList<ConnectorScopeDefinition> Scopes { get; }
}
