using System.Collections.Immutable;

namespace Hive.Domain.Ai;

public sealed record AiProviderMetadata
{
    public AiProviderMetadata(
        string providerId,
        string modelId,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ProviderId = AiContractGuards.RequireText(providerId, nameof(providerId));
        ModelId = AiContractGuards.RequireText(modelId, nameof(modelId));
        Metadata = AiContractGuards.SnapshotMetadata(metadata, nameof(metadata));
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}
