using System.Globalization;

namespace Hive.Infrastructure.Governance;

/// <summary>
/// One readable problem found while parsing an action-domain catalog YAML document.
/// </summary>
public sealed record ActionDomainCatalogParseError
{
    public ActionDomainCatalogParseError(
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

    public string FilePath { get; }

    public string FieldPath { get; }

    public int? Line { get; }

    public int? Column { get; }

    public string Message { get; }

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
