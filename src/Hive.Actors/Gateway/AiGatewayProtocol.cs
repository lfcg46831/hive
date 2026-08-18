using Hive.Domain.Ai;

namespace Hive.Actors.Gateway;

/// <summary>
/// The closed protocol of the sharded AI gateway entity (US-F1-05-T07). Every message is a
/// remote/sharded message and therefore travels in the versionable ADR-007 System.Text.Json format
/// under a stable manifest; no CLR type name ever reaches the wire.
/// </summary>
public abstract record AiGatewayProviderCommand
{
    private protected AiGatewayProviderCommand(string correlationId)
    {
        CorrelationId = AiGatewayProtocolText.Require(correlationId, nameof(correlationId));
    }

    /// <summary>Caller-owned correlation of a single gateway call (US-F0-07-T12).</summary>
    public string CorrelationId { get; }
}

/// <summary>
/// Executes one gateway call on the provider entity. The request is already assembled by the
/// caller; the entity delegates it unchanged to <see cref="IAiGateway"/>.
/// </summary>
public sealed record CompleteAiGatewayCall : AiGatewayProviderCommand
{
    public CompleteAiGatewayCall(string correlationId, AiGatewayRequest request)
        : base(correlationId)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public AiGatewayRequest Request { get; }
}

/// <summary>
/// Cancels a call already in flight on the entity. It is the wire form of the caller's
/// cancellation token: the entity cancels the in-flight gateway call and answers
/// <see cref="AiGatewayCallCanceled"/>, so cancellation still wins over any concurrent response.
/// </summary>
public sealed record CancelAiGatewayCall : AiGatewayProviderCommand
{
    public CancelAiGatewayCall(string correlationId)
        : base(correlationId)
    {
    }
}

/// <summary>The terminal reply of a completed call: the gateway response, unchanged.</summary>
public sealed record AiGatewayCallCompleted
{
    public AiGatewayCallCompleted(string correlationId, AiGatewayResponse response)
    {
        CorrelationId = AiGatewayProtocolText.Require(correlationId, nameof(correlationId));
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }

    public string CorrelationId { get; }

    public AiGatewayResponse Response { get; }
}

/// <summary>
/// The terminal reply of a canceled call. It carries no response and no audit: cancellation
/// produces neither, exactly as in US-F1-05-T04/T06.
/// </summary>
public sealed record AiGatewayCallCanceled
{
    public AiGatewayCallCanceled(string correlationId)
    {
        CorrelationId = AiGatewayProtocolText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

/// <summary>
/// The sharded-message envelope of the AI gateway (US-F1-05-T07). It pairs the destination
/// provider key with an address-free command, so the transport carries the addressing while the
/// command stays a pure intent — the same convention as <c>PositionEnvelope</c>.
/// </summary>
public sealed record AiGatewayEnvelope
{
    public AiGatewayEnvelope(string providerKey, AiGatewayProviderCommand command)
    {
        ProviderKey = AiGatewayProtocolText.Require(providerKey, nameof(providerKey));
        Command = command ?? throw new ArgumentNullException(nameof(command));
    }

    /// <summary>The sharded entity id; see <see cref="AiGatewayEntityId"/>.</summary>
    public string ProviderKey { get; }

    /// <summary>The address-free command handed to the entity once the envelope is unwrapped.</summary>
    public AiGatewayProviderCommand Command { get; }

    public static AiGatewayEnvelope For(string providerKey, AiGatewayProviderCommand command) =>
        new(providerKey, command);

    /// <summary>Wraps a call for the entity that owns the request's effective provider.</summary>
    public static AiGatewayEnvelope ForRequest(string correlationId, AiGatewayRequest request) =>
        new(AiGatewayEntityId.ForRequest(request), new CompleteAiGatewayCall(correlationId, request));
}

internal static class AiGatewayProtocolText
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
