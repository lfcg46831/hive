using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

public sealed record AuditExportProvider
{
    public AuditExportProvider(string providerId, string modelId)
    {
        ProviderId = AuditExportContractGuards.Text(
            providerId,
            nameof(providerId));
        ModelId = AuditExportContractGuards.Text(modelId, nameof(modelId));
    }

    [JsonPropertyName("provider_id")]
    public string ProviderId { get; }

    [JsonPropertyName("model_id")]
    public string ModelId { get; }
}
