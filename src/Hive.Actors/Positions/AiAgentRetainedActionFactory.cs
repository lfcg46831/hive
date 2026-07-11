using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hive.Actors.Serialization;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal static class AiAgentRetainedActionFactory
{
    public static RetainAction Create(AiAgentActionGateResult result, DateTimeOffset retainedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        var retention = result.Retention
            ?? throw new ArgumentException("A retained gate result must carry a retention intent.", nameof(result));

        var payload = CanonicalPayload(retention.Candidate);
        var facts = CanonicalFacts(result.Facts);
        var fingerprintValue = "sha256:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{(int)retention.Candidate.Kind}\n{retention.Candidate.Selector}\n{payload}\n{facts}")))
            .ToLowerInvariant();
        var fingerprint = ActionFingerprint.From(fingerprintValue);
        var id = RetainedActionId.From(DeterministicGuid.FromName(
            $"retained-action|{retention.OrganizationId.Value}|{retention.PositionId.Value}|{retention.CorrelationId}|{fingerprint.Value}"));
        var policies = retention.GovernanceMessages
            .OfType<ApprovalRequest>()
            .Select(request => request.Policy);

        return new RetainAction(new PersistedRetainedAction(
            id,
            fingerprint,
            retention.Candidate.Kind == ActionDomainActionKind.Tool
                ? RetainedActionKind.Tool
                : RetainedActionKind.OrganizationalMessage,
            retention.Candidate.Selector,
            payload,
            facts,
            retention.CorrelationId,
            retention.OrganizationId,
            retention.PositionId,
            retention.ThreadId,
            retention.SourceMessageId,
            retention.DirectiveId,
            retention.ParentDirectiveId,
            retention.Code,
            retainedAt,
            policies,
            retention.GovernanceMessages));
    }

    private static string CanonicalPayload(AiAgentActionCandidate candidate)
    {
        if (candidate.Message is { } message)
        {
            return Encoding.UTF8.GetString(OrgMessageJsonFormat.Serialize(message));
        }

        var tool = candidate.ToolCall
            ?? throw new InvalidOperationException("Retained tool action has no tool call.");
        var arguments = tool.Arguments
            .OrderBy(argument => argument.Key, StringComparer.Ordinal)
            .ToDictionary(argument => argument.Key, argument => argument.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(new { tool.Id, tool.Name, Arguments = arguments });
    }

    private static string CanonicalFacts(ActionFacts? facts)
    {
        if (facts is null)
        {
            return "{}";
        }

        var attributes = facts.Attributes
            .OrderBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                attribute => attribute.Key,
                attribute => new { Kind = attribute.Value.Kind.ToString(), Value = attribute.Value.CanonicalValue },
                StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            Action = facts.Action.ToString(),
            facts.SelectorValue,
            Attributes = attributes,
        });
    }
}
