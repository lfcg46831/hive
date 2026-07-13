using Hive.Domain.Ai;
using Microsoft.Extensions.AI;
using System.ClientModel;
using System.Net;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Real AI gateway adapter (US-F0-07-T05b). Maps the provider-neutral HIVE
/// contract onto the <see cref="IChatClient"/> abstraction of
/// <c>Microsoft.Extensions.AI</c> and back, consuming the validated
/// <see cref="RealAiGatewayProviderSettings"/> produced by US-F0-07-T05a.
/// <para>
/// Its single responsibility is request normalization plus response, error,
/// timeout and cancellation mapping. It does not resolve position configuration
/// beyond the supplied settings, apply authorization/budget/fallback/retry
/// (US-F0-07-T08-T11), emit audit, build the concrete provider
/// <see cref="IChatClient"/> (OpenAI/Azure) or decide default activation
/// (US-F0-07-T05c). Provider-declared cost and catalog-based estimates are
/// normalized without exposing SDK types. The secret credential never leaves
/// the settings instance and is never copied into a request, response, error
/// message or diagnostic.
/// </para>
/// </summary>
internal sealed class RealAiGatewayProvider : IAiGatewayProvider
{
    private readonly IChatClient _chatClient;
    private readonly RealAiGatewayRequestNormalizer _normalizer = new();
    private readonly RealAiGatewayResponseNormalizer _responseNormalizer = new();
    private readonly RealAiGatewayProviderSettings _settings;
    private readonly TimeProvider _timeProvider;

    public RealAiGatewayProvider(
        IChatClient chatClient,
        RealAiGatewayProviderSettings settings,
        TimeProvider? timeProvider = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Caller-initiated cancellation propagates, never converted to a result.
        cancellationToken.ThrowIfCancellationRequested();

        var outputNegotiation = AiOutputConstraintNegotiator.Negotiate(
            request.OutputConstraint,
            _settings.OutputCapabilities);
        if (outputNegotiation.IsFailure)
        {
            return Failed(
                request,
                AiGatewayErrorCode.OutputConstraintUnsupported,
                outputNegotiation.FailureReason!,
                isRetryable: false);
        }

        var outputConstraintMode = outputNegotiation.EffectiveMode;
        var normalized = _normalizer.Normalize(
            request,
            _settings,
            outputConstraintMode);

        var timeout = request.Timeout ?? _settings.Timeout;
        using var deadlineCts = timeout is not null
            ? new CancellationTokenSource(timeout.Value, _timeProvider)
            : null;
        using var providerCts = deadlineCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCts.Token)
            : null;
        var effectiveToken = providerCts?.Token ?? cancellationToken;

        ChatResponse response;
        Task<ChatResponse>? providerTask = null;
        try
        {
            providerTask = _chatClient.GetResponseAsync(
                normalized.Messages,
                normalized.Options,
                effectiveToken);
            response = await providerTask
                .WaitAsync(effectiveToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to cancel: propagate rather than swallow.
            ObserveLateCompletion(providerTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            // WaitAsync makes the deadline coercive even when the provider ignores
            // its token. A late fault is observed but cannot re-enter normalization
            // or create another functional result.
            ObserveLateCompletion(providerTask);
            return Failed(
                request,
                ProviderFailure.Timeout(statusCode: null),
                outputConstraintMode);
        }
        catch (ClientResultException ex)
        {
            return Failed(
                request,
                ProviderFailure.FromStatus(ex.Status),
                outputConstraintMode);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } statusCode)
        {
            return Failed(
                request,
                ProviderFailure.FromStatus((int)statusCode),
                outputConstraintMode);
        }
        catch (HttpRequestException)
        {
            return Failed(
                request,
                ProviderFailure.ProviderUnavailable(statusCode: null),
                outputConstraintMode);
        }
        catch (Exception)
        {
            return Failed(
                request,
                ProviderFailure.ProviderUnavailable(statusCode: null),
                outputConstraintMode);
        }

        return _responseNormalizer.Normalize(
            request,
            response,
            _settings,
            outputConstraintMode).Response;
    }

    private static void ObserveLateCompletion(Task? providerTask)
    {
        if (providerTask is null)
        {
            return;
        }

        _ = providerTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private AiGatewayResponse Failed(
        AiGatewayRequest request,
        ProviderFailure failure,
        AiOutputConstraintMode? outputConstraintMode) =>
        Failed(
            request,
            failure.Code,
            failure.Message,
            failure.IsRetryable,
            outputConstraintMode);

    private AiGatewayResponse Failed(
        AiGatewayRequest request,
        AiGatewayErrorCode code,
        string message,
        bool isRetryable,
        AiOutputConstraintMode? outputConstraintMode = null) =>
        AiGatewayResponse.Failed(
            new AiGatewayError(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                code,
                message,
                isRetryable,
                new AiProviderMetadata(
                    request.Provider?.ProviderId ?? _settings.DefaultProvider.ProviderId,
                    request.Provider?.ModelId ?? _settings.DefaultProvider.ModelId)),
            outputConstraintMode);

    private sealed record ProviderFailure(
        AiGatewayErrorCode Code,
        string Message,
        bool IsRetryable)
    {
        public static ProviderFailure FromStatus(int statusCode)
        {
            return statusCode switch
            {
                401 or 403 => new(
                    AiGatewayErrorCode.CredentialsMissing,
                    BuildMessage(AiGatewayErrorCode.CredentialsMissing, statusCode),
                    IsRetryable: false),
                408 or 504 => Timeout(statusCode),
                429 => new(
                    AiGatewayErrorCode.QuotaExceeded,
                    BuildMessage(AiGatewayErrorCode.QuotaExceeded, statusCode),
                    IsRetryable: true),
                >= 400 and < 500 => new(
                    AiGatewayErrorCode.ProviderRejected,
                    BuildMessage(AiGatewayErrorCode.ProviderRejected, statusCode),
                    IsRetryable: false),
                >= 500 and < 600 => ProviderUnavailable(statusCode),
                _ => ProviderUnavailable(statusCode),
            };
        }

        public static ProviderFailure Timeout(int? statusCode) =>
            new(
                AiGatewayErrorCode.Timeout,
                BuildMessage(AiGatewayErrorCode.Timeout, statusCode),
                IsRetryable: true);

        public static ProviderFailure ProviderUnavailable(int? statusCode) =>
            new(
                AiGatewayErrorCode.ProviderUnavailable,
                BuildMessage(AiGatewayErrorCode.ProviderUnavailable, statusCode),
                IsRetryable: true);

        private static string BuildMessage(AiGatewayErrorCode code, int? statusCode)
        {
            var wireCode = AiGatewayErrorCodeContract.ToWireValue(code);
            return statusCode is { } status
                ? $"AI gateway real provider failed ({wireCode}, status {status})."
                : $"AI gateway real provider failed ({wireCode}).";
        }
    }
}
