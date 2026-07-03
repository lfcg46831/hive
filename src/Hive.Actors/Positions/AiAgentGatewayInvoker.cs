using Hive.Domain.Ai;

namespace Hive.Actors.Positions;

internal interface IAiAgentGatewayInvoker
{
    Task<AiAgentGatewayInvocationResult> InvokeAsync(
        AiAgentGatewayInvocation invocation,
        CancellationToken cancellationToken = default);
}

internal sealed record AiAgentGatewayInvocation
{
    public AiAgentGatewayInvocation(string correlationId, AiGatewayRequest request)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public string CorrelationId { get; }

    public AiGatewayRequest Request { get; }
}

internal sealed record AiAgentGatewayInvocationResult
{
    private AiAgentGatewayInvocationResult(string correlationId, AiGatewayResponse response)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }

    public string CorrelationId { get; }

    public AiGatewayResponse Response { get; }

    public bool IsSuccess => Response.IsSuccess;

    public bool IsFailure => Response.IsFailure;

    public AiGatewayError? FailureReason => Response.Error;

    public static AiAgentGatewayInvocationResult FromResponse(
        string correlationId,
        AiGatewayResponse response) =>
        new(correlationId, response);
}

internal sealed record GetAiDirectiveGatewayInvocation
{
    public GetAiDirectiveGatewayInvocation(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveGatewayInvocationQueryResult
{
    private AiDirectiveGatewayInvocationQueryResult(
        string correlationId,
        AiAgentGatewayInvocationResult? result)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Result = result;
    }

    public string CorrelationId { get; }

    public AiAgentGatewayInvocationResult? Result { get; }

    public bool Found => Result is not null;

    public static AiDirectiveGatewayInvocationQueryResult FoundResult(
        AiAgentGatewayInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiDirectiveGatewayInvocationQueryResult(
            result.CorrelationId,
            result);
    }

    public static AiDirectiveGatewayInvocationQueryResult Missing(string correlationId) =>
        new(correlationId, result: null);
}

internal sealed class AiAgentGatewayInvoker : IAiAgentGatewayInvoker
{
    private readonly IAiGateway _gateway;

    public AiAgentGatewayInvoker(IAiGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    public async Task<AiAgentGatewayInvocationResult> InvokeAsync(
        AiAgentGatewayInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            var response = await _gateway
                .CompleteAsync(invocation.Request, cancellationToken)
                .ConfigureAwait(false);

            return AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                response ?? UnexpectedFailure(invocation.Request));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                UnexpectedFailure(invocation.Request));
        }
    }

    private static AiGatewayResponse UnexpectedFailure(AiGatewayRequest request) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ProviderUnavailable,
            "AI gateway invocation failed before returning a structured response.",
            isRetryable: true,
            request.Provider));
}

internal sealed class UnavailableAiAgentGatewayInvoker : IAiAgentGatewayInvoker
{
    public static UnavailableAiAgentGatewayInvoker Instance { get; } = new();

    private UnavailableAiAgentGatewayInvoker()
    {
    }

    public Task<AiAgentGatewayInvocationResult> InvokeAsync(
        AiAgentGatewayInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var request = invocation.Request;
        var response = AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ConfigurationInvalid,
            "AI agent gateway invoker is not configured for this actor.",
            isRetryable: false,
            request.Provider));

        return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
            invocation.CorrelationId,
            response));
    }
}

internal static class AiAgentGatewayText
{
    public static string Require(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }
}
