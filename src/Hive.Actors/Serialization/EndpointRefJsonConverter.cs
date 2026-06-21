using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Serialization;

/// <summary>
/// Serializes the <see cref="EndpointRef"/> discriminated union with an explicit <c>kind</c>
/// discriminator instead of System.Text.Json polymorphic type names, keeping payloads decoupled
/// from CLR type names (ADR-007). The closed variant set mirrors §9.2: position, organization
/// owner and the two F0 system endpoints.
/// </summary>
internal sealed class EndpointRefJsonConverter : JsonConverter<EndpointRef>
{
    private const string KindProperty = "kind";
    private const string PositionIdProperty = "positionId";
    private const string SystemProperty = "system";

    private const string PositionKind = "position";
    private const string OrganizationOwnerKind = "organization-owner";
    private const string SystemKind = "system";

    private const string SchedulerWire = "scheduler";
    private const string DomainEventsWire = "domain-events";

    public override EndpointRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an object for EndpointRef.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (!root.TryGetProperty(KindProperty, out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("EndpointRef is missing a string 'kind' discriminator.");
        }

        var kind = kindElement.GetString();
        switch (kind)
        {
            case PositionKind:
                return new PositionEndpointRef(ReadPositionId(root));

            case OrganizationOwnerKind:
                return new OrganizationOwnerEndpointRef();

            case SystemKind:
                return new SystemEndpointRef(ReadSystemKind(root));

            default:
                throw new JsonException($"'{kind}' is not a known EndpointRef kind.");
        }
    }

    public override void Write(Utf8JsonWriter writer, EndpointRef value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        switch (value)
        {
            case PositionEndpointRef position:
                writer.WriteString(KindProperty, PositionKind);
                writer.WriteString(PositionIdProperty, position.PositionId.Value);
                break;

            case OrganizationOwnerEndpointRef:
                writer.WriteString(KindProperty, OrganizationOwnerKind);
                break;

            case SystemEndpointRef system:
                writer.WriteString(KindProperty, SystemKind);
                writer.WriteString(SystemProperty, ToWire(system.Kind));
                break;

            default:
                throw new JsonException($"'{value.GetType().Name}' is not a supported EndpointRef variant.");
        }

        writer.WriteEndObject();
    }

    private static PositionId ReadPositionId(JsonElement root)
    {
        if (!root.TryGetProperty(PositionIdProperty, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("A position endpoint requires a string 'positionId'.");
        }

        try
        {
            return PositionId.From(element.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Position endpoint has an invalid 'positionId'.", exception);
        }
    }

    private static SystemEndpointKind ReadSystemKind(JsonElement root)
    {
        if (!root.TryGetProperty(SystemProperty, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("A system endpoint requires a string 'system' value.");
        }

        return element.GetString() switch
        {
            SchedulerWire => SystemEndpointKind.Scheduler,
            DomainEventsWire => SystemEndpointKind.DomainEvents,
            var other => throw new JsonException($"'{other}' is not a known system endpoint."),
        };
    }

    private static string ToWire(SystemEndpointKind kind) =>
        kind switch
        {
            SystemEndpointKind.Scheduler => SchedulerWire,
            SystemEndpointKind.DomainEvents => DomainEventsWire,
            _ => throw new JsonException($"'{kind}' is not a supported system endpoint."),
        };
}
