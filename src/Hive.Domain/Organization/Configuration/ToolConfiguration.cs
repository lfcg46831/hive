namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// One authorized tool/connector of an occupant (§6.2 <c>occupant.tools[]</c>): the
/// <see cref="Connector"/> name and its <see cref="Scope"/> entries restricting what the connector
/// may access. Whether the connector and scopes are authorized is validated later (US-F0-05-T06).
/// </summary>
public sealed record ToolConfiguration
{
    /// <summary>Creates an authorization for <paramref name="connector"/> restricted to <paramref name="scope"/>.</summary>
    public ToolConfiguration(string connector, IReadOnlyList<string>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(connector);

        Connector = connector;
        Scope = scope ?? Array.Empty<string>();
    }

    /// <summary>The connector name (for example <c>http</c> or <c>files</c>).</summary>
    public string Connector { get; }

    /// <summary>The scope entries restricting the connector; empty when unrestricted in the document.</summary>
    public IReadOnlyList<string> Scope { get; }
}
