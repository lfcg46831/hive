using System.Security.Cryptography;
using System.Text;

namespace Hive.Evaluation.Tooling.Evaluation;

public sealed record EvaluationDirectiveIds(
    Guid MessageId,
    Guid ThreadId,
    Guid DirectiveId)
{
    public static EvaluationDirectiveIds FromSeed(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            throw new ArgumentException(
                "A non-empty evaluation seed is required.",
                nameof(seed));
        }

        return new EvaluationDirectiveIds(
            CreateGuid(seed, "message"),
            CreateGuid(seed, "thread"),
            CreateGuid(seed, "directive"));
    }

    private static Guid CreateGuid(string seed, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{purpose}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed record EvaluationDirectiveEndpointRef(
    string Kind,
    string PositionId);

public sealed record EvaluationDirectiveRequest(
    string MessageId,
    EvaluationDirectiveEndpointRef From,
    EvaluationDirectiveEndpointRef To,
    string ThreadId,
    string Priority,
    int SchemaVersion,
    DateTimeOffset SentAt,
    DateTimeOffset? Deadline,
    string DirectiveId,
    string? ParentDirectiveId,
    string Objective,
    string Context);

public sealed record EvaluationDirectiveSubmission(
    string OrganizationId,
    string RelativePath,
    EvaluationDirectiveRequest Request);

internal static class EvaluationDirectiveFactory
{
    public const string OrganizationId = "acme-delivery";
    private const string SourcePositionId = "delivery-lead";
    private const string DestinationPositionId = "bug-triage";

    private const string Objective =
        "Triage the submitted production issue and report severity, missing information, and next action.";

    private const string CompletionCriteria = """
        Completion criteria:
        - Severity and user impact are classified from the provided facts.
        - Missing information is called out explicitly when the context is incomplete.
        - The next action is returned as a report or escalated when it is outside the position authority.
        """;

    public static EvaluationDirectiveSubmission Create(
        EvaluationDirectiveIds ids,
        DateTimeOffset sentAt,
        string context,
        string? observationInstruction = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var contextSections = new List<string>
        {
            context.TrimEnd(),
            CompletionCriteria,
        };
        if (!string.IsNullOrWhiteSpace(observationInstruction))
        {
            contextSections.Add(observationInstruction.Trim());
        }

        var request = new EvaluationDirectiveRequest(
            ids.MessageId.ToString("D"),
            new EvaluationDirectiveEndpointRef("position", SourcePositionId),
            new EvaluationDirectiveEndpointRef("position", DestinationPositionId),
            ids.ThreadId.ToString("D"),
            "high",
            SchemaVersion: 1,
            SentAt: sentAt,
            Deadline: null,
            ids.DirectiveId.ToString("D"),
            ParentDirectiveId: null,
            Objective,
            string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                contextSections));

        return new EvaluationDirectiveSubmission(
            OrganizationId,
            $"/api/v1/organizations/{OrganizationId}/directives",
            request);
    }
}
