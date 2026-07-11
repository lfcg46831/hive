using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hive.Actors.Serialization;
using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Actors.Positions;

internal sealed record RetainedActionFingerprintMaterial(
    ActionFingerprint Fingerprint,
    string CanonicalPayload,
    string CanonicalFacts,
    string CanonicalDocument);

internal static class RetainedActionFingerprintFactory
{
    private const int SchemaVersion = 1;

    public static RetainedActionFingerprintMaterial Create(
        AiAgentActionCandidate candidate,
        ActionFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var payload = CanonicalPayload(candidate);
        var canonicalFacts = CanonicalFacts(facts);
        using var payloadDocument = JsonDocument.Parse(payload);
        using var factsDocument = JsonDocument.Parse(canonicalFacts);
        var canonicalDocument = CanonicalJson.Serialize(new
        {
            schemaVersion = SchemaVersion,
            kind = Kind(candidate.Kind),
            selector = candidate.Selector,
            payload = payloadDocument.RootElement,
            facts = factsDocument.RootElement,
        });
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalDocument)))
            .ToLowerInvariant();

        return new RetainedActionFingerprintMaterial(
            ActionFingerprint.From(ActionFingerprint.AlgorithmPrefix + digest),
            payload,
            canonicalFacts,
            canonicalDocument);
    }

    private static string CanonicalPayload(AiAgentActionCandidate candidate)
    {
        if (candidate.Message is { } message)
        {
            return CanonicalJson.Canonicalize(OrgMessageJsonFormat.Serialize(message));
        }

        var tool = candidate.ToolCall
            ?? throw new InvalidOperationException("Retained tool action has no tool call.");
        return CanonicalJson.Serialize(new
        {
            tool.Id,
            tool.Name,
            tool.Arguments,
        });
    }

    private static string CanonicalFacts(ActionFacts? facts) =>
        facts is null
            ? "{}"
            : CanonicalJson.Serialize(new
            {
                Action = Kind(facts.Action),
                facts.SelectorValue,
                Attributes = facts.Attributes.ToDictionary(
                    attribute => attribute.Key,
                    attribute => new
                    {
                        Kind = AttributeKind(attribute.Value.Kind),
                        Value = attribute.Value.CanonicalValue,
                    },
                    StringComparer.Ordinal),
            });

    private static string Kind(ActionDomainActionKind kind) =>
        kind switch
        {
            ActionDomainActionKind.Tool => "tool",
            ActionDomainActionKind.OrganizationalMessage => "organizational-message",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown action kind."),
        };

    private static string AttributeKind(ActionAttributeValueKind kind) =>
        kind switch
        {
            ActionAttributeValueKind.String => "string",
            ActionAttributeValueKind.Boolean => "boolean",
            ActionAttributeValueKind.Integer => "integer",
            ActionAttributeValueKind.Decimal => "decimal",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown action attribute kind."),
        };
}
