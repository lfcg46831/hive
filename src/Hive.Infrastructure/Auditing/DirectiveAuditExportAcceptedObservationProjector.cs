using System.Text;
using System.Text.Json;
using Hive.Domain.Auditing;

namespace Hive.Infrastructure.Auditing;

/// <summary>
/// Experimental audit/export adapter projection. It recognizes the bounded observation marker
/// supplied by an external observer and retains only its compact string-label envelope. The
/// normal directive runtime remains unaware of its vocabulary and the superseded message is never
/// returned from this boundary.
/// </summary>
internal static class DirectiveAuditExportAcceptedObservationProjector
{
    private const string EnvelopeMarker = "hive-evaluation-v1:";
    private const int MaximumContentUtf8Bytes = 4 * 1_024;
    private const int MaximumEntries = 32;
    private const int MaximumLabelsPerEntry = 32;
    private const int MaximumEntryIdLength = 128;
    private const int MaximumLabelLength = 256;

    public static DirectiveAuditExportObservationData? TryProject(
        DirectiveAuditExportMessageData? supersededResult)
    {
        if (supersededResult is null)
        {
            return null;
        }

        string? payload;
        try
        {
            using var message = JsonDocument.Parse(supersededResult.Content);
            var propertyName = supersededResult.MessageType switch
            {
                "Report" => "Body",
                "Escalation" => "Context",
                _ => null,
            };
            payload = propertyName is not null &&
                message.RootElement.ValueKind == JsonValueKind.Object &&
                message.RootElement.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null)
        {
            return null;
        }

        var first = payload.IndexOf(EnvelopeMarker, StringComparison.Ordinal);
        if (first < 0 || payload.IndexOf(
                EnvelopeMarker,
                first + EnvelopeMarker.Length,
                StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        try
        {
            using var envelope = JsonDocument.Parse(payload[(first + EnvelopeMarker.Length)..]);
            var root = envelope.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            var containers = rootProperties
                .Where(property => property.NameEquals("dimensions"))
                .ToArray();
            if (root.ValueKind != JsonValueKind.Object ||
                rootProperties.Length != 1 ||
                containers.Length != 1 ||
                containers[0].Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var entries = containers[0].Value.EnumerateObject().ToArray();
            if (entries.Length == 0 ||
                entries.Length > MaximumEntries ||
                entries.Select(entry => entry.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != entries.Length)
            {
                return null;
            }

            var projected = new SortedDictionary<string, IReadOnlyList<string>>(
                StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!IsBoundedText(entry.Name, MaximumEntryIdLength) ||
                    entry.Value.ValueKind != JsonValueKind.Array ||
                    entry.Value.GetArrayLength() > MaximumLabelsPerEntry)
                {
                    return null;
                }

                var labels = new List<string>(entry.Value.GetArrayLength());
                foreach (var value in entry.Value.EnumerateArray())
                {
                    var label = value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                    if (!IsBoundedText(label, MaximumLabelLength) ||
                        labels.Contains(label!, StringComparer.Ordinal))
                    {
                        return null;
                    }

                    labels.Add(label!);
                }

                projected.Add(entry.Name, labels);
            }

            var content = CanonicalContent(projected);
            return Encoding.UTF8.GetByteCount(content) <= MaximumContentUtf8Bytes
                ? new DirectiveAuditExportObservationData(
                    DirectiveAuditExportObservationData.CurrentContractVersion,
                    content)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CanonicalContent(
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("dimensions");
            writer.WriteStartObject();
            foreach (var (entryId, labels) in entries)
            {
                writer.WritePropertyName(entryId);
                writer.WriteStartArray();
                foreach (var label in labels)
                {
                    writer.WriteStringValue(label);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
