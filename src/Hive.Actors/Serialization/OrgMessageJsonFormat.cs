using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Serialization;

/// <summary>
/// The canonical System.Text.Json format for the organizational message protocol (ADR-007),
/// shared by the Akka serializer and reusable by persisted events/snapshots. The records in
/// <c>Hive.Domain.Messaging</c> are the single source of truth; this layer only adds explicit
/// converters for the value objects and discriminated unions and keeps the domain free of any
/// serialization concern.
/// </summary>
/// <remarks>
/// Versionability rests on: (1) the explicit <c>SchemaVersion</c> field carried by the envelope;
/// (2) tolerant reads that ignore unknown properties but never apply silent defaults — missing
/// required fields are rejected by the domain constructors; and (3) the canonical textual wire
/// values already defined for the protocol enums and identities.
/// </remarks>
internal static class OrgMessageJsonFormat
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize(object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);
    }

    public static object Deserialize(string manifest, ReadOnlySpan<byte> payload)
    {
        var type = OrgMessageManifests.ForManifest(manifest);
        return JsonSerializer.Deserialize(payload, type, Options)
            ?? throw new JsonException($"Payload for manifest '{manifest}' deserialized to null.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Tolerant reads: unknown properties are ignored and cosmetic input variations are
            // accepted, but required fields are still enforced by the domain constructors.
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { DropComputedProperties },
            },
        };

        // Protocol enums as canonical wire values (§9.5).
        options.Converters.Add(new PriorityJsonConverter());
        options.Converters.Add(new MessageChannelJsonConverter());
        options.Converters.Add(new MessageStateJsonConverter());
        options.Converters.Add(new RejectionReasonJsonConverter());
        options.Converters.Add(new ReportKindJsonConverter());

        // Discriminated union of endpoints (§9.2).
        options.Converters.Add(new EndpointRefJsonConverter());

        // Message with a read-only immutable collection that System.Text.Json cannot bind through
        // its constructor (see EscalationJsonConverter).
        options.Converters.Add(new EscalationJsonConverter());

        // Structural (string) identity value objects (§9.1).
        options.Converters.Add(new StructuralIdJsonConverter<OrganizationId>(OrganizationId.From, id => id.Value));
        options.Converters.Add(new StructuralIdJsonConverter<UnitId>(UnitId.From, id => id.Value));
        options.Converters.Add(new StructuralIdJsonConverter<PositionId>(PositionId.From, id => id.Value));
        options.Converters.Add(new StructuralIdJsonConverter<OccupantId>(OccupantId.From, id => id.Value));
        options.Converters.Add(new StructuralIdJsonConverter<ApprovalPolicyRef>(ApprovalPolicyRef.From, id => id.Value));

        // Distributed (Guid) identity value objects (§9.1).
        options.Converters.Add(new GuidIdJsonConverter<MessageId>(MessageId.From, id => id.Value));
        options.Converters.Add(new GuidIdJsonConverter<ThreadId>(ThreadId.From, id => id.Value));
        options.Converters.Add(new GuidIdJsonConverter<DirectiveId>(DirectiveId.From, id => id.Value));

        return options;
    }

    /// <summary>
    /// Removes get-only properties that are not bound to a constructor parameter (e.g. the derived
    /// <see cref="OrgMessage.Channel"/>) from the serialized shape. Such properties are computed
    /// from the concrete type and cannot be read back, so emitting them would only add redundant,
    /// potentially drifting data to the payload.
    /// </summary>
    private static void DropComputedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var constructors = typeInfo.Type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
        {
            return;
        }

        var parameterNames = constructors[0]
            .GetParameters()
            .Select(parameter => parameter.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            var property = typeInfo.Properties[index];
            if (property.Set is null && !parameterNames.Contains(property.Name))
            {
                typeInfo.Properties.RemoveAt(index);
            }
        }
    }
}
