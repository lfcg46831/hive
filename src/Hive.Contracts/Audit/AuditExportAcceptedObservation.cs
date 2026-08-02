using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

/// <summary>
/// Optional bounded observation retained from an accepted organizational result that was
/// superseded before emission. Its content is an opaque JSON envelope and never contains the
/// superseded message body, prompt, provider output or reasoning.
/// </summary>
public sealed record AuditExportAcceptedObservation
{
    public AuditExportAcceptedObservation(
        int contractVersion,
        string mediaType,
        int contentLengthBytes,
        string sha256,
        string content)
    {
        if (contractVersion != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                "Only accepted-observation contract version 1 is supported.");
        }

        if (!string.Equals(
                mediaType,
                AuditExportContract.AcceptedObservationMediaType,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Accepted observation media type must be '{AuditExportContract.AcceptedObservationMediaType}'.",
                nameof(mediaType));
        }

        ContractVersion = contractVersion;
        MediaType = mediaType;
        if (contentLengthBytes > AuditExportContractLimits.MaxAcceptedObservationContentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                contentLengthBytes,
                $"Accepted observation content cannot exceed {AuditExportContractLimits.MaxAcceptedObservationContentBytes} UTF-8 bytes.");
        }

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
                "Accepted observation SHA-256 does not match its content.",
                nameof(sha256));
        }
    }

    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; }

    [JsonPropertyName("content_length_bytes")]
    public int ContentLengthBytes { get; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; }

    [JsonPropertyName("content")]
    public string Content { get; }

    public static AuditExportAcceptedObservation Create(
        int contractVersion,
        string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        return new AuditExportAcceptedObservation(
            contractVersion,
            AuditExportContract.AcceptedObservationMediaType,
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            content);
    }
}
