using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

public sealed record AuditExportResult
{
    public AuditExportResult(
        string messageType,
        int schemaVersion,
        string mediaType,
        int contentLengthBytes,
        string sha256,
        string content,
        AuditExportAcceptedObservation? acceptedObservation = null)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (!string.Equals(
                mediaType,
                AuditExportContract.ResultMediaType,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Result media type must be '{AuditExportContract.ResultMediaType}'.",
                nameof(mediaType));
        }

        MessageType = AuditExportContractGuards.Text(
            messageType,
            nameof(messageType));
        SchemaVersion = schemaVersion;
        MediaType = mediaType;
        Content = AuditExportContractGuards.CanonicalJsonContent(
            content,
            contentLengthBytes,
            nameof(content));
        ContentLengthBytes = contentLengthBytes;
        Sha256 = AuditExportContractGuards.Text(
            sha256,
            nameof(sha256),
            maxLength: 64);

        var actualHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Content)))
            .ToLowerInvariant();
        if (!string.Equals(Sha256, actualHash, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Result SHA-256 does not match its content.",
                nameof(sha256));
        }

        AcceptedObservation = acceptedObservation;
    }

    [JsonPropertyName("message_type")]
    public string MessageType { get; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; }

    [JsonPropertyName("content_length_bytes")]
    public int ContentLengthBytes { get; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; }

    [JsonPropertyName("content")]
    public string Content { get; }

    [JsonPropertyName("accepted_observation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AuditExportAcceptedObservation? AcceptedObservation { get; }

    public static AuditExportResult Create(
        string messageType,
        int schemaVersion,
        string content,
        AuditExportAcceptedObservation? acceptedObservation = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        return new AuditExportResult(
            messageType,
            schemaVersion,
            AuditExportContract.ResultMediaType,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            content,
            acceptedObservation);
    }
}
