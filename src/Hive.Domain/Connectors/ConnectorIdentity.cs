using System.Text.RegularExpressions;

namespace Hive.Domain.Connectors;

/// <summary>Stable, provider-neutral connector identity.</summary>
public sealed record ConnectorId
{
    private static readonly Regex Format = new(
        "^[a-z0-9]+(?:[.-][a-z0-9]+)*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private ConnectorId(string value) => Value = value;

    public string Value { get; }

    public static ConnectorId From(string value)
    {
        var canonical = ConnectorContractGuards.RequireText(value, nameof(value));
        if (!Format.IsMatch(canonical))
        {
            throw new ArgumentException(
                "Connector id must be a lowercase dot- or kebab-separated token.",
                nameof(value));
        }

        return new ConnectorId(canonical);
    }

    public override string ToString() => Value;
}

/// <summary>A strict SemVer 2.0 connector version, retained in its canonical wire form.</summary>
public sealed record ConnectorVersion
{
    private static readonly Regex Format = new(
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)" +
        "(?:-((?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)" +
        "(?:\\.(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        "(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private ConnectorVersion(
        string value,
        string core,
        string? prerelease,
        string? buildMetadata)
    {
        Value = value;
        Core = core;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    public string Value { get; }

    public string Core { get; }

    public string? Prerelease { get; }

    public string? BuildMetadata { get; }

    public static ConnectorVersion Parse(string value)
    {
        var canonical = ConnectorContractGuards.RequireText(value, nameof(value));
        var match = Format.Match(canonical);
        if (!match.Success)
        {
            throw new ArgumentException(
                "Connector version must be a valid Semantic Version 2.0 value.",
                nameof(value));
        }

        var core = string.Concat(match.Groups[1].Value, ".", match.Groups[2].Value, ".", match.Groups[3].Value);
        return new ConnectorVersion(
            canonical,
            core,
            match.Groups[4].Success ? match.Groups[4].Value : null,
            match.Groups[5].Success ? match.Groups[5].Value : null);
    }

    public override string ToString() => Value;
}
