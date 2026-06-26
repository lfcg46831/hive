using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;

namespace Hive.Actors.Serialization;

/// <summary>
/// Base converter that renders a protocol enum as its canonical lowercase/kebab-case wire value
/// (§9.5) by delegating to the enum's own wire contract. Unknown or undefined values fail with a
/// <see cref="JsonException"/> so the closed sets are never silently widened on the wire.
/// </summary>
internal abstract class WireEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    protected abstract string ToWire(TEnum value);

    protected abstract bool TryParseWire(string? value, out TEnum result);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string wire value for {typeof(TEnum).Name}.");
        }

        var raw = reader.GetString();
        if (!TryParseWire(raw, out var value))
        {
            throw new JsonException($"'{raw}' is not a valid {typeof(TEnum).Name} wire value.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ToWire(value));
    }
}

internal sealed class PriorityJsonConverter : WireEnumJsonConverter<Priority>
{
    protected override string ToWire(Priority value) => PriorityContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out Priority result) =>
        PriorityContract.TryParseWireValue(value, out result);
}

internal sealed class MessageChannelJsonConverter : WireEnumJsonConverter<MessageChannel>
{
    protected override string ToWire(MessageChannel value) => MessageChannelContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out MessageChannel result) =>
        MessageChannelContract.TryParseWireValue(value, out result);
}

internal sealed class MessageStateJsonConverter : WireEnumJsonConverter<MessageState>
{
    protected override string ToWire(MessageState value) => MessageStateContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out MessageState result) =>
        MessageStateContract.TryParseWireValue(value, out result);
}

internal sealed class RejectionReasonJsonConverter : WireEnumJsonConverter<RejectionReason>
{
    protected override string ToWire(RejectionReason value) => RejectionReasonContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out RejectionReason result) =>
        RejectionReasonContract.TryParseWireValue(value, out result);
}

internal sealed class ReportKindJsonConverter : WireEnumJsonConverter<ReportKind>
{
    protected override string ToWire(ReportKind value) => ReportKindContract.ToWireValue(value);

    protected override bool TryParseWire(string? value, out ReportKind result) =>
        ReportKindContract.TryParseWireValue(value, out result);
}

internal sealed class OccupantTypeJsonConverter : WireEnumJsonConverter<OccupantType>
{
    protected override string ToWire(OccupantType value) =>
        value switch
        {
            OccupantType.AiAgent => "ai-agent",
            OccupantType.Human => "human",
            _ => throw new JsonException($"'{value}' is not a supported occupant type."),
        };

    protected override bool TryParseWire(string? value, out OccupantType result)
    {
        switch (value)
        {
            case "ai-agent":
                result = OccupantType.AiAgent;
                return true;

            case "human":
                result = OccupantType.Human;
                return true;

            default:
                result = default;
                return false;
        }
    }
}
