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
/// (US-F0-07-T08-T11), compute real cost, emit audit, build the concrete provider
/// <see cref="IChatClient"/> (OpenAI/Azure) or decide default activation
/// (US-F0-07-T05c). The secret credential never leaves the settings instance and
/// is never copied into a request, response, error message or diagnostic.
/// </para>
/// </summary>
internal sealed class RealAiGatewayProvider : IAiGatewayProvider
{
    private readonly IChatClient _chatClient;
    private readonly RealAiGatewayRequestNormalizer _normalizer = new();
    private readonly RealAiGatewayResponseNormalizer _responseNormalizer = new();
    private readonly RealAiGatewayProviderSettings _settings;

    public RealAiGatewayProvider(
        IChatClient chatClient,
        RealAiGatewayProviderSettings settings)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Caller-initiated cancellation propagates, never converted to a result.
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = _normalizer.Normalize(request, _settings);

        var timeout = request.Timeout ?? _settings.Timeout;
        using var timeoutCts = timeout is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeoutCts?.CancelAfter(timeout!.Value);
        var effectiveToken = timeoutCts?.Token ?? cancellationToken;

        ChatResponse response;
        try
        {
            response = await _chatClient
                .GetResponseAsync(
                    normalized.Messages,
                    normalized.Options,
                    effectiveToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to cancel: propagate rather than swallow.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Cancellation that did not originate from the caller is the internal
            // timeout firing (or a provider-initiated abort): a retryable timeout.
            return Failed(request, ProviderFailure.Timeout(statusCode: null));
        }
        catch (ClientResultException ex)
        {
            return Failed(request, ProviderFailure.FromStatus(ex.Status));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } statusCode)
        {
            return Failed(request, ProviderFailure.FromStatus((int)statusCode));
        }
        catch (HttpRequestException)
        {
            return Failed(request, ProviderFailure.ProviderUnavailable(statusCode: null));
        }
        catch (Exception)
        {
            return Failed(request, ProviderFailure.ProviderUnavailable(statusCode: null));
        }

        return _responseNormalizer.Normalize(request, response, _settings).Response;
    }

    private AiGatewayResponse Failed(AiGatewayRequest request, ProviderFailure failure) =>
        Failed(request, failure.Code, failure.Message, failure.IsRetryable);

    private AiGatewayResponse Failed(
        AiGatewayRequest request,
        AiGatewayErrorCode code,
        string message,
        bool isRetryable) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            message,
            isRetryable,
            new AiProviderMetadata(
                request.Provider?.ProviderId ?? _settings.DefaultProvider.ProviderId,
                request.Provider?.ModelId ?? _settings.DefaultProvider.ModelId)));

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
