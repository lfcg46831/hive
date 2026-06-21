using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Actors.Serialization;

/// <summary>
/// Explicit converter for <see cref="Escalation"/>. System.Text.Json cannot bind a constructor
/// parameter (<c>IEnumerable&lt;string&gt; optionsConsidered</c>) to a get-only
/// <c>ImmutableArray&lt;string&gt;</c> property — read-only immutable collections are treated as
/// non-deserializable, so the parameter has no valid binding target. This converter round-trips the
/// record through a small mutable shape with the same canonical field names, while the domain
/// constructor still enforces every invariant (including the non-null options list).
/// </summary>
internal sealed class EscalationJsonConverter : JsonConverter<Escalation>
{
    public override Escalation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize<EscalationData>(ref reader, options)
            ?? throw new JsonException("Escalation payload deserialized to null.");

        return new Escalation(
            dto.Id!,
            dto.OrganizationId!,
            dto.From!,
            dto.To!,
            dto.Thread!,
            dto.Priority,
            dto.SchemaVersion,
            dto.SentAt,
            dto.Deadline,
            dto.Issue!,
            dto.Context!,
            dto.OptionsConsidered!);
    }

    public override void Write(Utf8JsonWriter writer, Escalation value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var dto = new EscalationData
        {
            Id = value.Id,
            OrganizationId = value.OrganizationId,
            From = value.From,
            To = value.To,
            Thread = value.Thread,
            Priority = value.Priority,
            SchemaVersion = value.SchemaVersion,
            SentAt = value.SentAt,
            Deadline = value.Deadline,
            Issue = value.Issue,
            Context = value.Context,
            OptionsConsidered = value.OptionsConsidered.ToList(),
        };

        JsonSerializer.Serialize(writer, dto, options);
    }

    /// <summary>
    /// Mutable mirror of <see cref="Escalation"/> with a settable options list, used only as the
    /// serialization shape. Field names and order match the canonical record so the wire payload is
    /// identical to the other message types.
    /// </summary>
    private sealed class EscalationData
    {
        public MessageId? Id { get; set; }

        public OrganizationId? OrganizationId { get; set; }

        public EndpointRef? From { get; set; }

        public EndpointRef? To { get; set; }

        public ThreadId? Thread { get; set; }

        public Priority Priority { get; set; }

        public int SchemaVersion { get; set; }

        public DateTimeOffset SentAt { get; set; }

        public DateTimeOffset? Deadline { get; set; }

        public string? Issue { get; set; }

        public string? Context { get; set; }

        public List<string>? OptionsConsidered { get; set; }
    }
}
