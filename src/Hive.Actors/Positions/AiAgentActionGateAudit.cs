using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;

namespace Hive.Actors.Positions;

internal abstract class AiAgentActionGateBase : IAiAgentActionGate
{
    private readonly IJourneyAuditLog _auditLog;
    private readonly Func<DateTimeOffset> _utcNow;

    protected AiAgentActionGateBase(
        IJourneyAuditLog auditLog,
        Func<DateTimeOffset>? utcNow = null)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<AiAgentActionGateResult> EvaluateAsync(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidate);

        var result = await EvaluateCoreAsync(context, candidate, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException(
                "The action gate returned no result to the audited base pipeline.");
        }

        if (!ReferenceEquals(candidate, result.Candidate))
        {
            throw new InvalidOperationException(
                "The action gate must preserve the exact candidate instance.");
        }

        _auditLog.Append(AiAgentActionGateAuditRecordFactory.Create(
            context,
            result,
            _utcNow()));

        return result;
    }

    protected abstract ValueTask<AiAgentActionGateResult> EvaluateCoreAsync(
        AiDirectiveExecutionContext context,
        AiAgentActionCandidate candidate,
        CancellationToken cancellationToken);
}

internal static class AiAgentActionGateAuditRecordFactory
{
    internal const string ActionArgumentsRedaction = "action.arguments:omitted";
    internal const string ActionFactsRedaction = "action.facts.values:omitted";
    internal const string ActionIdentityRedaction = "action.instance.raw:hashed";
    internal const string ActionMessageRedaction = "action.message.payload:omitted";
    internal const string ActingUnderRedaction = "acting_under.raw:discarded";
    internal const string GateCodeRedaction = "gate.code:normalized";

    private const string NoValue = "none";
    private const string RedactedSelector = "redacted";
    private const string DecideGate = "decide";
    private const string EscalateGate = "escalate";
    private const string HumanApprovalGate = "human-approval";
    private const string FailClosedGate = "fail-closed";

    public static JourneyAuditRecord Create(
        AiDirectiveExecutionContext context,
        AiAgentActionGateResult result,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        var candidate = result.Candidate
            ?? throw new ArgumentException(
                "An action gate audit result must carry its candidate.",
                nameof(result));
        var actionKind = ActionKind(candidate.Kind);
        var actionSelector = SafeSelector(result);
        var gateOutcome = GateOutcome(result.Outcome);
        var effectiveGate = EffectiveGate(result);
        var gateCode = SafeCode(result);
        var allowedAuthorityKey = AllowedAuthorityKey(result);
        var instanceDigest = ActionInstanceDigest(candidate);
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["correlationId"] = context.CorrelationId,
            ["parentDirectiveId"] = context.Directive.ParentDirectiveId?.Value.ToString("N") ?? NoValue,
            ["actionKind"] = actionKind,
            ["actionSelector"] = actionSelector,
            ["actionInstanceDigest"] = instanceDigest,
            ["gateOutcome"] = gateOutcome,
            ["effectiveGate"] = effectiveGate,
            ["gateCode"] = gateCode,
            ["actingUnderState"] = ActingUnderState(candidate.ActingUnder.State),
            ["actingUnderCode"] = candidate.ActingUnder.Code,
            ["matchCount"] = (result.Resolution?.Matches.Length ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            ["approvalRequirementCount"] = (result.Resolution?.RequiredApprovals.Length ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            ["redactions"] = Redactions(),
        };

        if (allowedAuthorityKey is not null)
        {
            payload["allowedAuthorityKey"] = allowedAuthorityKey.Value;
        }

        var discriminator = string.Join(
            "|",
            "action-gate:v1",
            actionKind,
            actionSelector,
            instanceDigest,
            gateOutcome,
            gateCode,
            candidate.ActingUnder.Code,
            allowedAuthorityKey?.Value ?? NoValue);

        return JourneyAuditRecord.Create(
            JourneyAuditStage.ActionGateEvaluated,
            result.IsAllowed
                ? JourneyAuditOutcome.Succeeded
                : JourneyAuditOutcome.Rejected,
            context.OrganizationId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            context.Directive.DirectiveId,
            context.PositionId,
            reasonCode: gateCode,
            messageType: candidate.Kind == ActionDomainActionKind.OrganizationalMessage
                ? candidate.Selector
                : null,
            payload: payload,
            occurredAtUtc: occurredAtUtc,
            idempotencyDiscriminator: discriminator);
    }

    private static string ActionKind(ActionDomainActionKind kind) =>
        kind switch
        {
            ActionDomainActionKind.Tool => "tool",
            ActionDomainActionKind.OrganizationalMessage => "organizational-message",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown action kind."),
        };

    private static string GateOutcome(AiAgentActionGateOutcome outcome) =>
        outcome switch
        {
            AiAgentActionGateOutcome.Allowed => "allowed",
            AiAgentActionGateOutcome.RetainedForEscalation => "retained-for-escalation",
            AiAgentActionGateOutcome.RetainedForHumanApproval => "retained-for-human-approval",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown action gate outcome."),
        };

    private static string ActingUnderState(ActingUnderDeclarationState state) =>
        state switch
        {
            ActingUnderDeclarationState.Declared => "declared",
            ActingUnderDeclarationState.Missing => "missing",
            ActingUnderDeclarationState.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown acting-under state."),
        };

    private static string SafeSelector(AiAgentActionGateResult result) =>
        result.Candidate.Kind == ActionDomainActionKind.OrganizationalMessage
        || result.Resolution is not null
            ? result.Candidate.Selector
            : RedactedSelector;

    private static string SafeCode(AiAgentActionGateResult result)
        => AiAgentActionGateCodes.Normalize(result.Code);

    private static string EffectiveGate(AiAgentActionGateResult result)
    {
        if (result.Outcome == AiAgentActionGateOutcome.Allowed)
        {
            if (result.Resolution?.Outcome != ActionGateOutcome.Allowed)
            {
                throw InvalidResultShape("An allowed action must carry an allowed domain resolution.");
            }

            return DecideGate;
        }

        if (result.Resolution is null)
        {
            return FailClosedGate;
        }

        return (result.Outcome, result.Resolution.Outcome) switch
        {
            (AiAgentActionGateOutcome.RetainedForEscalation, ActionGateOutcome.EscalationRequired) =>
                EscalateGate,
            (AiAgentActionGateOutcome.RetainedForHumanApproval, ActionGateOutcome.HumanApprovalRequired) =>
                HumanApprovalGate,
            _ => throw InvalidResultShape(
                "The retained action outcome does not match its domain resolution."),
        };
    }

    private static AuthorityKey? AllowedAuthorityKey(AiAgentActionGateResult result)
    {
        if (!result.IsAllowed)
        {
            if (result.Resolution?.AllowedAuthorityKey is not null)
            {
                throw InvalidResultShape(
                    "A retained action cannot expose an allowed authority key.");
            }

            return null;
        }

        return result.Resolution?.AllowedAuthorityKey
            ?? throw InvalidResultShape(
                "An allowed action audit requires the authority key that permitted it.");
    }

    private static string ActionInstanceDigest(AiAgentActionCandidate candidate)
    {
        var instanceIdentity = candidate.Kind switch
        {
            ActionDomainActionKind.Tool => candidate.ToolCall?.Id
                ?? throw InvalidResultShape("A tool action must carry a tool-call identity."),
            ActionDomainActionKind.OrganizationalMessage => candidate.Message?.Id.Value.ToString("N")
                ?? throw InvalidResultShape("A message action must carry a message identity."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(candidate),
                candidate.Kind,
                "Unknown action kind."),
        };
        var canonicalIdentity = string.Join(
            "|",
            "action-instance:v1",
            ActionKind(candidate.Kind),
            candidate.Selector,
            instanceIdentity);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
        return "sha256:" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Redactions() =>
        string.Join(
            ",",
            ActionArgumentsRedaction,
            ActionFactsRedaction,
            ActionIdentityRedaction,
            ActionMessageRedaction,
            ActingUnderRedaction,
            GateCodeRedaction);

    private static InvalidOperationException InvalidResultShape(string message) =>
        new(message);
}
