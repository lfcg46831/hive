using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Serialization;

/// <summary>
/// Explicit persisted-protocol converter for the bounded checkpoint contract. Its immutable
/// collection properties intentionally differ from the validating constructor inputs, so a DTO is
/// used to preserve constructor validation on every read.
/// </summary>
internal sealed class DirectiveCheckpointJsonConverter : JsonConverter<DirectiveCheckpoint>
{
    public override DirectiveCheckpoint Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize<DirectiveCheckpointData>(ref reader, options)
            ?? throw new JsonException("DirectiveCheckpoint payload deserialized to null.");
        var plan = dto.Plan
            ?? throw new JsonException("DirectiveCheckpoint plan is required.");
        var correlation = dto.Correlation
            ?? throw new JsonException("DirectiveCheckpoint correlation is required.");

        return new DirectiveCheckpoint(
            dto.ContractVersion,
            dto.Revision,
            new DirectiveCheckpointPlan(
                plan.ContractVersion,
                (plan.Subtasks ?? []).Select(subtask =>
                    new DirectiveCheckpointSubtask(
                        subtask.Sequence,
                        subtask.LocalId!,
                        subtask.Objective!,
                        subtask.CompletionCriteria ?? [],
                        subtask.EstimatedDuration))),
            new DirectiveCheckpointCorrelation(
                correlation.OrganizationId!,
                correlation.PositionId!,
                correlation.ThreadId!,
                correlation.DirectiveId!,
                correlation.ParentDirectiveId,
                correlation.PositionTaskId),
            (dto.CompletedSubtasks ?? []).Select(completed =>
                new CompletedDirectiveCheckpointSubtask(
                    completed.LocalId!,
                    (completed.EvidenceReferences ?? []).Select(reference =>
                        new OutcomeEvidenceReference(reference.Source, reference.Reference!)))),
            dto.Blockers,
            dto.NextSubtaskId);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DirectiveCheckpoint value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var dto = new DirectiveCheckpointData
        {
            ContractVersion = value.ContractVersion,
            Revision = value.Revision,
            Plan = new DirectiveCheckpointPlanData
            {
                ContractVersion = value.Plan.ContractVersion,
                Subtasks = value.Plan.Subtasks.Select(subtask =>
                    new DirectiveCheckpointSubtaskData
                    {
                        Sequence = subtask.Sequence,
                        LocalId = subtask.LocalId,
                        Objective = subtask.Objective,
                        CompletionCriteria = subtask.CompletionCriteria.ToList(),
                        EstimatedDuration = subtask.EstimatedDuration,
                    }).ToList(),
            },
            Correlation = new DirectiveCheckpointCorrelationData
            {
                OrganizationId = value.Correlation.OrganizationId,
                PositionId = value.Correlation.PositionId,
                ThreadId = value.Correlation.ThreadId,
                DirectiveId = value.Correlation.DirectiveId,
                ParentDirectiveId = value.Correlation.ParentDirectiveId,
                PositionTaskId = value.Correlation.PositionTaskId,
            },
            CompletedSubtasks = value.CompletedSubtasks.Select(completed =>
                new CompletedDirectiveCheckpointSubtaskData
                {
                    LocalId = completed.LocalId,
                    EvidenceReferences = completed.EvidenceReferences.Select(reference =>
                        new OutcomeEvidenceReferenceData
                        {
                            Source = reference.Source,
                            Reference = reference.Reference,
                        }).ToList(),
                }).ToList(),
            Blockers = value.Blockers.ToList(),
            NextSubtaskId = value.NextSubtaskId,
        };

        JsonSerializer.Serialize(writer, dto, options);
    }

    private sealed class DirectiveCheckpointData
    {
        public int ContractVersion { get; set; }

        public int Revision { get; set; }

        public DirectiveCheckpointPlanData? Plan { get; set; }

        public DirectiveCheckpointCorrelationData? Correlation { get; set; }

        public List<CompletedDirectiveCheckpointSubtaskData>? CompletedSubtasks { get; set; }

        public List<OutcomeBlocker>? Blockers { get; set; }

        public string? NextSubtaskId { get; set; }
    }

    private sealed class DirectiveCheckpointPlanData
    {
        public int ContractVersion { get; set; }

        public List<DirectiveCheckpointSubtaskData>? Subtasks { get; set; }
    }

    private sealed class DirectiveCheckpointSubtaskData
    {
        public int Sequence { get; set; }

        public string? LocalId { get; set; }

        public string? Objective { get; set; }

        public List<string>? CompletionCriteria { get; set; }

        public TimeSpan EstimatedDuration { get; set; }
    }

    private sealed class DirectiveCheckpointCorrelationData
    {
        public OrganizationId? OrganizationId { get; set; }

        public PositionId? PositionId { get; set; }

        public ThreadId? ThreadId { get; set; }

        public DirectiveId? DirectiveId { get; set; }

        public DirectiveId? ParentDirectiveId { get; set; }

        public PositionTaskId? PositionTaskId { get; set; }
    }

    private sealed class CompletedDirectiveCheckpointSubtaskData
    {
        public string? LocalId { get; set; }

        public List<OutcomeEvidenceReferenceData>? EvidenceReferences { get; set; }
    }

    private sealed class OutcomeEvidenceReferenceData
    {
        public OutcomeEvidenceSource Source { get; set; }

        public string? Reference { get; set; }
    }
}
