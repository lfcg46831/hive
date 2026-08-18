using Hive.Domain.Ai;

namespace Hive.Actors.Gateway;

/// <summary>
/// Addressing contract for the sharded AI gateway entity (US-F1-05-T07). The sharded
/// <c>entityId</c> is the effective <see cref="AiProviderMetadata.ProviderId"/> of the request, so
/// the queue, rate limiter and circuit breaker of a provider exist exactly once in the cluster —
/// the same partition the in-process buckets of US-F1-05-T03/T05 already use.
/// </summary>
public static class AiGatewayEntityId
{
    /// <summary>
    /// Stable Cluster Sharding entity type name. It is part of the placement contract and must be
    /// identical on every node.
    /// </summary>
    public const string EntityTypeName = "ai-gateway-provider";

    /// <summary>
    /// The single canonical bucket for legacy requests that carry no effective provider, mirroring
    /// the <c>null</c> provider key of the in-process limiter/circuit buckets. It is not a valid
    /// provider identifier, so it can never collide with a configured provider.
    /// </summary>
    public const string LocalProviderKey = "local-bucket";

    /// <summary>
    /// Resolves the sharded entity id for a request. Requests without an effective provider all
    /// converge on <see cref="LocalProviderKey"/>.
    /// </summary>
    public static string ForProvider(AiProviderMetadata? provider) =>
        provider is null || string.IsNullOrWhiteSpace(provider.ProviderId)
            ? LocalProviderKey
            : provider.ProviderId;

    /// <summary>Resolves the sharded entity id for a gateway request.</summary>
    public static string ForRequest(AiGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ForProvider(request.Provider);
    }
}
