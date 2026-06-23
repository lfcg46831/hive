using System.Globalization;

namespace Hive.Infrastructure.Organization.Configuration;

/// <summary>
/// One readable problem found while parsing an organization YAML document (US-F0-05-T04): the
/// <see cref="FilePath"/> the document was read from, the dotted <see cref="FieldPath"/> of the
/// offending field (for example <c>positions[1].occupant.ai.max_tokens</c>, or <c>$</c> for the
/// document root), the 1-based <see cref="Line"/>/<see cref="Column"/> of the YAML node when the
/// parser could locate it, and a human-readable <see cref="Message"/>.
/// </summary>
/// <remarks>
/// This type carries <em>parse-level</em> problems only — malformed YAML, a missing required block,
/// a value of the wrong shape, an unknown enum literal or an unparseable number. Semantic rules
/// (uniqueness, cross-references and structure) are out of scope here and are enforced by
/// US-F0-05-T05–T07 over the already-typed model.
/// </remarks>
public sealed record OrganizationConfigurationParseError
{
    /// <summary>Creates an error at <paramref name="fieldPath"/> in <paramref name="filePath"/>.</summary>
    public OrganizationConfigurationParseError(
        string filePath,
        string fieldPath,
        string message,
        int? line = null,
        int? column = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(fieldPath);
        ArgumentNullException.ThrowIfNull(message);

        FilePath = filePath;
        FieldPath = fieldPath;
        Message = message;
        Line = line;
        Column = column;
    }

    /// <summary>The path of the document the error was found in.</summary>
    public string FilePath { get; }

    /// <summary>The dotted location of the offending field, or <c>$</c> for the document root.</summary>
    public string FieldPath { get; }

    /// <summary>The 1-based line of the offending YAML node, when known.</summary>
    public int? Line { get; }

    /// <summary>The 1-based column of the offending YAML node, when known.</summary>
    public int? Column { get; }

    /// <summary>The human-readable description of the problem.</summary>
    public string Message { get; }

    /// <summary>
    /// Renders the error as <c>{file}({line},{column}): {fieldPath}: {message}</c>, omitting the
    /// position when it could not be located. The leading <c>{file}(line,column)</c> shape matches
    /// the conventional compiler/MSBuild diagnostic format so editors can navigate to it.
    /// </summary>
    public override string ToString()
    {
        var position = Line is { } line
            ? Column is { } column
                ? string.Create(CultureInfo.InvariantCulture, $"({line},{column})")
                : string.Create(CultureInfo.InvariantCulture, $"({line})")
            : string.Empty;

        return $"{FilePath}{position}: {FieldPath}: {Message}";
    }
}
