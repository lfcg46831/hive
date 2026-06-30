using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

public sealed class AiGateway : IAiGateway
{
    private readonly IAiGatewayProvider _provider;

    public AiGateway(IAiGatewayProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var policyResult = ApplyPolicy(request);
        return policyResult.Error is { } error
            ? Task.FromResult(AiGatewayResponse.Failed(error))
            : _provider.CompleteAsync(policyResult.Request!, cancellationToken);
    }

    private static AiGatewayPolicyResult ApplyPolicy(AiGatewayRequest request)
    {
        var policy = request.Policy;
        if (policy is null)
        {
            return AiGatewayPolicyResult.Success(request);
        }

        var providerResult = ResolveProvider(request, policy);
        if (providerResult.Error is { } providerError)
        {
            return AiGatewayPolicyResult.Failure(providerError);
        }

        var effectiveProvider = providerResult.Provider!;

        if (!policy.HasAvailableBudget)
        {
            return AiGatewayPolicyResult.Failure(Failed(
                request,
                AiGatewayErrorCode.BudgetInsufficient,
                "AI gateway budget is insufficient for this position.",
                effectiveProvider));
        }

        if (policy.AllowedProcessingModes.Length > 0 &&
            (request.ProcessingMode is not { } mode ||
             !policy.AllowedProcessingModes.Contains(mode)))
        {
            return AiGatewayPolicyResult.Failure(Failed(
                request,
                AiGatewayErrorCode.ConfigurationInvalid,
                "AI gateway processing mode is not allowed for this position.",
                effectiveProvider));
        }

        foreach (var tool in request.Tools)
        {
            if (!policy.AuthorizedTools.Contains(tool.Name, StringComparer.Ordinal))
            {
                return AiGatewayPolicyResult.Failure(Failed(
                    request,
                    AiGatewayErrorCode.ToolNotAuthorized,
                    $"AI gateway tool '{tool.Name}' is not authorized for this position.",
                    effectiveProvider));
            }
        }

        var effectiveParameters = ApplyMaxOutputTokens(
            request.ModelParameters,
            policy.MaxOutputTokens);
        var effectiveTimeout = ApplyMaxTimeout(request.Timeout, policy.MaxTimeout);

        if (ReferenceEquals(effectiveProvider, request.Provider) &&
            ReferenceEquals(effectiveParameters, request.ModelParameters) &&
            effectiveTimeout == request.Timeout)
        {
            return AiGatewayPolicyResult.Success(request);
        }

        return AiGatewayPolicyResult.Success(new AiGatewayRequest(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            request.Content,
            request.SystemInstruction,
            request.ContextMessages,
            request.Tools,
            effectiveParameters,
            request.Metadata,
            effectiveProvider,
            request.ProcessingMode,
            effectiveTimeout,
            request.Policy));
    }

    private static AiGatewayPolicyProviderResult ResolveProvider(
        AiGatewayRequest request,
        AiGatewayPolicy policy)
    {
        if (request.Provider is null)
        {
            return policy.AuthorizedModels.Length == 1
                ? AiGatewayPolicyProviderResult.Success(policy.AuthorizedModels[0])
                : AiGatewayPolicyProviderResult.Failure(Failed(
                    request,
                    AiGatewayErrorCode.ConfigurationInvalid,
                    "AI gateway request does not identify an effective provider/model.",
                    provider: null));
        }

        if (policy.AuthorizedModels.Any(model =>
            SameProvider(model, request.Provider) &&
            SameModel(model, request.Provider)))
        {
            return AiGatewayPolicyProviderResult.Success(request.Provider);
        }

        if (!policy.AuthorizedModels.Any(model => SameProvider(model, request.Provider)))
        {
            return AiGatewayPolicyProviderResult.Failure(Failed(
                request,
                AiGatewayErrorCode.ProviderNotAuthorized,
                $"AI gateway provider '{request.Provider.ProviderId}' is not authorized for this position.",
                request.Provider));
        }

        return AiGatewayPolicyProviderResult.Failure(Failed(
            request,
            AiGatewayErrorCode.ModelNotAuthorized,
            $"AI gateway model '{request.Provider.ModelId}' is not authorized for provider '{request.Provider.ProviderId}'.",
            request.Provider));
    }

    private static AiModelParameters ApplyMaxOutputTokens(
        AiModelParameters parameters,
        int? maxOutputTokens)
    {
        if (maxOutputTokens is not { } limit)
        {
            return parameters;
        }

        if (parameters.MaxOutputTokens is { } requested && requested <= limit)
        {
            return parameters;
        }

        return new AiModelParameters(parameters.Temperature, limit);
    }

    private static TimeSpan? ApplyMaxTimeout(
        TimeSpan? timeout,
        TimeSpan? maxTimeout)
    {
        if (maxTimeout is not { } limit)
        {
            return timeout;
        }

        if (timeout is { } requested && requested <= limit)
        {
            return timeout;
        }

        return limit;
    }

    private static bool SameProvider(
        AiProviderMetadata left,
        AiProviderMetadata right) =>
        string.Equals(left.ProviderId, right.ProviderId, StringComparison.Ordinal);

    private static bool SameModel(
        AiProviderMetadata left,
        AiProviderMetadata right) =>
        string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal);

    private static AiGatewayError Failed(
        AiGatewayRequest request,
        AiGatewayErrorCode code,
        string message,
        AiProviderMetadata? provider) =>
        new(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            message,
            isRetryable: false,
            provider);

    private sealed record AiGatewayPolicyResult(
        AiGatewayRequest? Request,
        AiGatewayError? Error)
    {
        public static AiGatewayPolicyResult Success(AiGatewayRequest request) =>
            new(request, Error: null);

        public static AiGatewayPolicyResult Failure(AiGatewayError error) =>
            new(Request: null, error);
    }

    private sealed record AiGatewayPolicyProviderResult(
        AiProviderMetadata? Provider,
        AiGatewayError? Error)
    {
        public static AiGatewayPolicyProviderResult Success(AiProviderMetadata provider) =>
            new(provider, Error: null);

        public static AiGatewayPolicyProviderResult Failure(AiGatewayError error) =>
            new(Provider: null, error);
    }
}
