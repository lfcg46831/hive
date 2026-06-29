using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Non-secret resolution of provider/model, model parameters and timeout for a
/// concrete position. Produced by
/// <see cref="RealAiGatewayProviderSettings.Resolve(AiPositionRuntimeConfiguration?)"/>;
/// it never carries the secret credential, keeping secrets separate from the
/// provider-neutral model configuration.
/// </summary>
public sealed record RealAiGatewayEffectiveModel(
    AiProviderMetadata Provider,
    AiModelParameters Parameters,
    TimeSpan? Timeout);
