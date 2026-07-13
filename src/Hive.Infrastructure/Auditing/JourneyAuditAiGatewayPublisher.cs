using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using System.Globalization;

namespace Hive.Infrastructure.Auditing;

public sealed class JourneyAuditAiGatewayPublisher :
    IAiGatewayAuditPublisher,
    IAiGatewayDetailedAuditPublisher
{
    private const string DirectiveIdMetadataKey = "directive_id";

    private readonly IJourneyAuditLog _auditLog;

    public JourneyAuditAiGatewayPublisher(IJourneyAuditLog auditLog)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    public void Publish(AiGatewayAuditEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.GatewayCalled,
            Outcome(envelope.Result),
            envelope.OrganizationId,
            envelope.ThreadId,
            envelope.MessageId,
            directiveId: DirectiveIdFrom(envelope.Request.Metadata),
            positionId: envelope.PositionId,
            reasonCode: envelope.RejectionReason,
            provider: envelope.Provider,
            latency: envelope.Duration,
            payload: DetailedPayload(envelope),
            occurredAtUtc: envelope.CompletedAt));
    }

    public void Publish(AiGatewayCostAuditEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.GatewayCostRecorded,
            Outcome(@event.Result),
            @event.OrganizationId,
            @event.ThreadId,
            @event.MessageId,
            directiveId: @event.DirectiveId,
            positionId: @event.PositionId,
            reasonCode: @event.ErrorCode is null
                ? null
                : AiGatewayErrorCodeContract.ToWireValue(@event.ErrorCode.Value),
            provider: @event.Provider,
            usage: @event.Usage,
            cost: @event.Cost,
            latency: @event.Duration,
            payload: CostPayload(@event),
            occurredAtUtc: @event.CompletedAt));
    }

    private static JourneyAuditOutcome Outcome(AiGatewayCallResult result) =>
        result == AiGatewayCallResult.Succeeded
            ? JourneyAuditOutcome.Succeeded
            : JourneyAuditOutcome.Failed;

    private static Dictionary<string, string> DetailedPayload(AiGatewayAuditEnvelope envelope)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["redactions"] = string.Join(
                ",",
                envelope.Redactions.Select(redaction => $"{redaction.Path}:{redaction.Reason}")),
            ["toolCount"] = envelope.Request.Tools.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (envelope.Request.ProcessingMode is { } processingMode)
        {
            payload["processingMode"] = processingMode.ToString();
        }

        if (envelope.Response?.FinishReason is { } finishReason)
        {
            payload["finishReason"] = finishReason.ToString();
        }

        if (envelope.Error is { } error)
        {
            payload["errorCode"] = AiGatewayErrorCodeContract.ToWireValue(error.Code);
            payload["isRetryable"] = error.IsRetryable.ToString();
        }

        if (envelope.OutputConstraintMode is { } outputConstraintMode)
        {
            payload["outputConstraintMode"] =
                AiOutputConstraintModeContract.ToWireValue(outputConstraintMode);
        }

        return payload;
    }

    private static Dictionary<string, string> CostPayload(AiGatewayCostAuditEvent @event)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["result"] = @event.Result.ToString(),
            ["costStatus"] = AiCostStatusContract.ToWireValue(@event.CostStatus),
        };

        if (@event.IsRetryable is { } isRetryable)
        {
            payload["isRetryable"] = isRetryable.ToString();
        }

        if (@event.OutputConstraintMode is { } outputConstraintMode)
        {
            payload["outputConstraintMode"] =
                AiOutputConstraintModeContract.ToWireValue(outputConstraintMode);
        }

        if (@event.AppliedPricing is { } pricing)
        {
            payload["pricingVersion"] = pricing.Version;
            payload["pricingTokenUnit"] = pricing.TokenUnit.ToString(CultureInfo.InvariantCulture);
            payload["inputPricePerTokenUnit"] = pricing.InputPrice.ToString(CultureInfo.InvariantCulture);
            payload["outputPricePerTokenUnit"] = pricing.OutputPrice.ToString(CultureInfo.InvariantCulture);
            payload["pricingCurrency"] = pricing.Currency;
        }

        return payload;
    }

    private static DirectiveId? DirectiveIdFrom(
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(DirectiveIdMetadataKey, out var value))
        {
            return null;
        }

        return Guid.TryParse(value, out var parsed)
            ? DirectiveId.From(parsed)
            : null;
    }
}
