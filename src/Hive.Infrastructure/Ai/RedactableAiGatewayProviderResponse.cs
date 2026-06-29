namespace Hive.Infrastructure.Ai;

internal sealed record RedactableAiGatewayProviderResponse
{
    public RedactableAiGatewayProviderResponse(
        string providerId,
        string modelId,
        string? responseId,
        string? rawRepresentation,
        IReadOnlyDictionary<string, string>? additionalProperties,
        IEnumerable<string>? contentTypes)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException(
                "Provider id cannot be empty or whitespace.",
                nameof(providerId));
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException(
                "Model id cannot be empty or whitespace.",
                nameof(modelId));
        }

        ProviderId = providerId;
        ModelId = modelId;
        ResponseId = string.IsNullOrWhiteSpace(responseId) ? null : responseId;
        RawRepresentation = string.IsNullOrWhiteSpace(rawRepresentation)
            ? null
            : rawRepresentation;
        AdditionalProperties = additionalProperties is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(
                additionalProperties,
                StringComparer.Ordinal);
        ContentTypes = contentTypes?.ToArray() ?? [];
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string? ResponseId { get; }

    public string? RawRepresentation { get; }

    public IReadOnlyDictionary<string, string> AdditionalProperties { get; }

    public IReadOnlyList<string> ContentTypes { get; }
}
