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
    private const string ExecutionLimitsVersionMetadataKey = "hive.execution-limits-version";
    private const string ExecutionBudgetMetadataKey = "hive.execution-budget-ms";
    private const string PerCallTimeoutMetadataKey = "hive.per-call-timeout-ms";

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
            usage: envelope.Result == AiGatewayCallResult.Failed
                ? envelope.Usage
                : null,
            cost: envelope.Result == AiGatewayCallResult.Failed
                ? envelope.Cost
                : null,
            latency: envelope.Duration,
            payload: DetailedPayload(envelope),
            occurredAtUtc: envelope.CompletedAt,
            idempotencyDiscriminator: GatewayCallDiscriminator(
                envelope.Request.Metadata)));
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
            occurredAtUtc: @event.CompletedAt,
            idempotencyDiscriminator: GatewayCallDiscriminator(
                @event.Operation,
                @event.Iteration)));
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

        if (envelope.Request.Timeout is { } requestTimeout)
        {
            payload["requestTimeoutMilliseconds"] =
                requestTimeout.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture);
        }

        if (envelope.Request.ModelParameters.MaxOutputTokens is { } maxOutputTokens)
        {
            payload["maxOutputTokens"] =
                maxOutputTokens.ToString(CultureInfo.InvariantCulture);
        }

        AddExecutionLimitsPayload(payload, envelope.Request.Metadata);

        var finishReason =
            envelope.Response?.FinishReason ??
            envelope.Error?.Diagnostics?.FinishReason;
        if (finishReason is { } resolvedFinishReason)
        {
            payload["finishReason"] = resolvedFinishReason.ToString();
        }

        if (envelope.Error is { } error)
        {
            payload["errorCode"] = AiGatewayErrorCodeContract.ToWireValue(error.Code);
            payload["isRetryable"] = error.IsRetryable.ToString();
            if (error.Diagnostics?.ProviderStatusCode is { } providerStatusCode)
            {
                payload["providerStatusCode"] =
                    providerStatusCode.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (envelope.OutputConstraintMode is { } outputConstraintMode)
        {
            payload["outputConstraintMode"] =
                AiOutputConstraintModeContract.ToWireValue(outputConstraintMode);
        }

        AddGatewayCallIdentity(payload, envelope.Request.Metadata);

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

        if (@event.FinishReason is { } finishReason)
        {
            payload["finishReason"] = finishReason.ToString();
        }

        if (@event.ProviderStatusCode is { } providerStatusCode)
        {
            payload["providerStatusCode"] =
                providerStatusCode.ToString(CultureInfo.InvariantCulture);
        }

        if (@event.RequestTimeout is { } requestTimeout)
        {
            payload["requestTimeoutMilliseconds"] =
                requestTimeout.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture);
        }

        if (@event.MaxOutputTokens is { } maxOutputTokens)
        {
            payload["maxOutputTokens"] =
                maxOutputTokens.ToString(CultureInfo.InvariantCulture);
        }

        if (@event.ExecutionLimitsVersion is { } executionLimitsVersion)
        {
            payload["executionLimitsVersion"] = executionLimitsVersion.ToString(
                CultureInfo.InvariantCulture);
        }

        if (@event.ExecutionBudget is { } executionBudget)
        {
            payload["executionBudgetMilliseconds"] = executionBudget.TotalMilliseconds.ToString(
                "R",
                CultureInfo.InvariantCulture);
        }

        if (@event.PerCallTimeout is { } perCallTimeout)
        {
            payload["perCallTimeoutMilliseconds"] = perCallTimeout.TotalMilliseconds.ToString(
                "R",
                CultureInfo.InvariantCulture);
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

        AddGatewayCallIdentity(payload, @event.Operation, @event.Iteration);

        return payload;
    }

    private static void AddGatewayCallIdentity(
        IDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue("hive.operation", out var operation);
        var iteration = metadata.TryGetValue("iteration", out var value) &&
            int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) &&
            parsed > 0
                ? parsed
                : (int?)null;
        AddGatewayCallIdentity(payload, operation, iteration);
    }

    private static void AddExecutionLimitsPayload(
        IDictionary<string, string> payload,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue(ExecutionLimitsVersionMetadataKey, out var version))
        {
            payload["executionLimitsVersion"] = version;
        }

        if (metadata.TryGetValue(ExecutionBudgetMetadataKey, out var executionBudget))
        {
            payload["executionBudgetMilliseconds"] = executionBudget;
        }

        if (metadata.TryGetValue(PerCallTimeoutMetadataKey, out var perCallTimeout))
        {
            payload["perCallTimeoutMilliseconds"] = perCallTimeout;
        }
    }

    private static void AddGatewayCallIdentity(
        IDictionary<string, string> payload,
        string? operation,
        int? iteration)
    {
        if (!string.IsNullOrWhiteSpace(operation))
        {
            payload["operation"] = operation;
        }

        if (iteration is { } value)
        {
            payload["iteration"] = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string? GatewayCallDiscriminator(
        IReadOnlyDictionary<string, string> metadata)
    {
        metadata.TryGetValue("hive.operation", out var operation);
        var iteration = metadata.TryGetValue("iteration", out var value) &&
            int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) &&
            parsed > 0
                ? parsed
                : (int?)null;
        return GatewayCallDiscriminator(operation, iteration);
    }

    private static string? GatewayCallDiscriminator(string? operation, int? iteration) =>
        !string.IsNullOrWhiteSpace(operation) && iteration is { } value
            ? $"{operation}:{value.ToString(CultureInfo.InvariantCulture)}"
            : null;

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
