using Hive.Domain.Ai;
using Microsoft.Extensions.AI;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Real AI gateway adapter (US-F0-07-T05b). Maps the provider-neutral HIVE
/// contract onto the <see cref="IChatClient"/> abstraction of
/// <c>Microsoft.Extensions.AI</c> and back, consuming the validated
/// <see cref="RealAiGatewayProviderSettings"/> produced by US-F0-07-T05a.
/// <para>
/// Its single responsibility is mapping request, response, error, timeout and
/// cancellation. It does not resolve position configuration beyond the supplied
/// settings, apply authorization/budget/fallback/retry (US-F0-07-T06–T11),
/// compute real cost, emit audit, build the concrete provider
/// <see cref="IChatClient"/> (OpenAI/Azure) or decide default activation
/// (US-F0-07-T05c). The secret credential never leaves the settings instance and
/// is never copied into a request, response, error message or diagnostic.
/// </para>
/// </summary>
internal sealed class RealAiGatewayProvider : IAiGatewayProvider
{
    private readonly IChatClient _chatClient;
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

        var messages = BuildMessages(request);
        var options = BuildOptions(request);

        using var timeoutCts = _settings.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeoutCts?.CancelAfter(_settings.Timeout!.Value);
        var effectiveToken = timeoutCts?.Token ?? cancellationToken;

        ChatResponse response;
        try
        {
            response = await _chatClient
                .GetResponseAsync(messages, options, effectiveToken)
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
            return Failed(
                request,
                AiGatewayErrorCode.Timeout,
                "AI gateway real provider timed out waiting for the model response.",
                isRetryable: true);
        }
        catch (Exception ex)
        {
            return Failed(
                request,
                AiGatewayErrorCode.ProviderUnavailable,
                $"AI gateway real provider failed to complete the request: {ex.Message}",
                isRetryable: true);
        }

        return MapResponse(request, response);
    }

    private List<ChatMessage> BuildMessages(AiGatewayRequest request)
    {
        var messages = new List<ChatMessage>();

        if (request.SystemInstruction is { } systemInstruction)
        {
            messages.Add(new ChatMessage(ChatRole.System, systemInstruction));
        }

        foreach (var contextMessage in request.ContextMessages)
        {
            messages.Add(new ChatMessage(
                MapRole(contextMessage.Role),
                contextMessage.Content));
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Content));

        return messages;
    }

    private ChatOptions BuildOptions(AiGatewayRequest request)
    {
        var parameters = request.ModelParameters;

        return new ChatOptions
        {
            ModelId = _settings.DefaultProvider.ModelId,
            Temperature = parameters.Temperature is { } temperature
                ? (float)temperature
                : null,
            MaxOutputTokens = parameters.MaxOutputTokens,
        };
    }

    private AiGatewayResponse MapResponse(
        AiGatewayRequest request,
        ChatResponse response)
    {
        var toolCalls = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Select(MapToolCall)
            .ToList();

        var text = string.IsNullOrEmpty(response.Text) ? null : response.Text;

        if (text is null && toolCalls.Count == 0)
        {
            return Failed(
                request,
                AiGatewayErrorCode.InvalidProviderResponse,
                "AI gateway real provider returned neither text nor a tool call.",
                isRetryable: false);
        }

        var finishReason = MapFinishReason(response.FinishReason, toolCalls.Count > 0);
        var provider = ResolveProviderMetadata(response.ModelId);
        var usage = MapUsage(response.Usage);

        return AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            text,
            finishReason,
            provider,
            toolCalls.Count == 0 ? null : toolCalls,
            usage,
            cost: null);
    }

    private static AiToolCall MapToolCall(FunctionCallContent content)
    {
        IReadOnlyDictionary<string, object?>? arguments = content.Arguments is null
            ? null
            : new Dictionary<string, object?>(content.Arguments, StringComparer.Ordinal);

        return new AiToolCall(content.CallId, content.Name, arguments);
    }

    private AiProviderMetadata ResolveProviderMetadata(string? responseModelId) =>
        new(
            _settings.DefaultProvider.ProviderId,
            string.IsNullOrWhiteSpace(responseModelId)
                ? _settings.DefaultProvider.ModelId
                : responseModelId);

    private static AiFinishReason MapFinishReason(
        ChatFinishReason? finishReason,
        bool hasToolCalls)
    {
        if (finishReason is { } reason)
        {
            if (reason == ChatFinishReason.Stop)
            {
                return AiFinishReason.Stop;
            }

            if (reason == ChatFinishReason.Length)
            {
                return AiFinishReason.Length;
            }

            if (reason == ChatFinishReason.ToolCalls)
            {
                return AiFinishReason.ToolCalls;
            }

            if (reason == ChatFinishReason.ContentFilter)
            {
                return AiFinishReason.ContentFiltered;
            }

            return AiFinishReason.Unknown;
        }

        return hasToolCalls ? AiFinishReason.ToolCalls : AiFinishReason.Stop;
    }

    private static AiTokenUsage? MapUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new AiTokenUsage(
            ToInt(usage.InputTokenCount),
            ToInt(usage.OutputTokenCount),
            ToInt(usage.TotalTokenCount));
    }

    private static int? ToInt(long? value)
    {
        if (value is not { } count || count < 0)
        {
            return null;
        }

        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static ChatRole MapRole(AiGatewayMessageRole role) => role switch
    {
        AiGatewayMessageRole.System => ChatRole.System,
        AiGatewayMessageRole.User => ChatRole.User,
        AiGatewayMessageRole.Assistant => ChatRole.Assistant,
        AiGatewayMessageRole.Tool => ChatRole.Tool,
        _ => throw new ArgumentOutOfRangeException(
            nameof(role),
            role,
            "AI gateway message role is undefined."),
    };

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
                _settings.DefaultProvider.ProviderId,
                _settings.DefaultProvider.ModelId)));
}
