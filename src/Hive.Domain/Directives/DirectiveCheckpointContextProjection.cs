using System.Text;
using System.Text.Json;
using Hive.Domain.Outcomes;

namespace Hive.Domain.Directives;

public sealed record DirectiveCheckpointContextProjection
{
    internal DirectiveCheckpointContextProjection(string content, int utf8Bytes)
    {
        Content = content;
        Utf8Bytes = utf8Bytes;
    }

    public string Content { get; }

    public int Utf8Bytes { get; }
}

/// <summary>
/// Produces the complete canonical semantic checkpoint projection. It never truncates fields; an
/// oversized checkpoint fails closed and yields no partial context.
/// </summary>
public static class DirectiveCheckpointContextProjector
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    public static bool TryProject(
        DirectiveCheckpoint checkpoint,
        out DirectiveCheckpointContextProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteCheckpoint(writer, checkpoint);
        }

        if (stream.Length > DirectiveCheckpointContractLimits.MaximumContextProjectionUtf8Bytes)
        {
            projection = null;
            return false;
        }

        var content = Encoding.UTF8.GetString(stream.ToArray());
        projection = new DirectiveCheckpointContextProjection(content, checked((int)stream.Length));
        return true;
    }

    private static void WriteCheckpoint(Utf8JsonWriter writer, DirectiveCheckpoint checkpoint)
    {
        writer.WriteStartObject();
        writer.WriteNumber("contract_version", checkpoint.ContractVersion);
        writer.WriteNumber("revision", checkpoint.Revision);

        writer.WritePropertyName("correlation");
        writer.WriteStartObject();
        writer.WriteString("organization_id", checkpoint.Correlation.OrganizationId.ToString());
        writer.WriteString("position_id", checkpoint.Correlation.PositionId.ToString());
        writer.WriteString("thread_id", checkpoint.Correlation.ThreadId.ToString());
        writer.WriteString("directive_id", checkpoint.Correlation.DirectiveId.ToString());
        WriteNullableString(
            writer,
            "parent_directive_id",
            checkpoint.Correlation.ParentDirectiveId?.ToString());
        WriteNullableString(
            writer,
            "position_task_id",
            checkpoint.Correlation.PositionTaskId?.ToString());
        writer.WriteEndObject();

        writer.WritePropertyName("plan");
        writer.WriteStartObject();
        writer.WriteNumber("contract_version", checkpoint.Plan.ContractVersion);
        writer.WritePropertyName("subtasks");
        writer.WriteStartArray();
        foreach (var subtask in checkpoint.Plan.Subtasks)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", subtask.Sequence);
            writer.WriteString("local_id", subtask.LocalId);
            writer.WriteString("objective", subtask.Objective);
            writer.WritePropertyName("completion_criteria");
            writer.WriteStartArray();
            foreach (var criterion in subtask.CompletionCriteria)
            {
                writer.WriteStringValue(criterion);
            }

            writer.WriteEndArray();
            writer.WriteNumber(
                "estimated_duration_ms",
                checked((long)subtask.EstimatedDuration.TotalMilliseconds));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WritePropertyName("completed_subtasks");
        writer.WriteStartArray();
        foreach (var completed in checkpoint.CompletedSubtasks)
        {
            writer.WriteStartObject();
            writer.WriteString("local_id", completed.LocalId);
            writer.WritePropertyName("evidence_references");
            writer.WriteStartArray();
            foreach (var reference in completed.EvidenceReferences)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "source",
                    OutcomeEvidenceSourceContract.ToWireValue(reference.Source));
                writer.WriteString("reference", reference.Reference);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("blockers");
        writer.WriteStartArray();
        foreach (var blocker in checkpoint.Blockers)
        {
            writer.WriteStringValue(OutcomeBlockerContract.ToWireValue(blocker));
        }

        writer.WriteEndArray();
        WriteNullableString(writer, "next_subtask_id", checkpoint.NextSubtaskId);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}
