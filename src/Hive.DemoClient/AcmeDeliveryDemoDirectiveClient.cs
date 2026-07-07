namespace Hive.DemoClient;

public static class AcmeDeliveryDemoDirectiveClient
{
    private const string OrganizationId = "acme-delivery";
    private const string SubmissionBasePath = "/api/v1/organizations";
    private const string SourcePositionId = "ceo";
    private const string DestinationPositionId = "delivery-lead";

    private const string Objective =
        "Triage the submitted production issue and report severity, missing information, and next action.";

    private const string CompletionCriteria = """
        Completion criteria:
        - Severity and user impact are classified from the provided facts.
        - Missing information is called out explicitly when the context is incomplete.
        - The next action is returned as a report or escalated when it is outside the position authority.
        """;

    public static DemoDirectiveSubmission CreateTriageDirective(
        DemoDirectiveIds ids,
        DateTimeOffset sentAt,
        string context)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (string.IsNullOrWhiteSpace(context))
        {
            throw new ArgumentException(
                "Directive context is required.",
                nameof(context));
        }

        var request = new DemoDirectiveRequest(
            ids.MessageId.ToString("D"),
            new DemoDirectiveEndpointRef("position", SourcePositionId),
            new DemoDirectiveEndpointRef("position", DestinationPositionId),
            ids.ThreadId.ToString("D"),
            "high",
            SchemaVersion: 1,
            SentAt: sentAt,
            Deadline: null,
            ids.DirectiveId.ToString("D"),
            ParentDirectiveId: null,
            Objective,
            AppendCompletionCriteria(context));

        return new DemoDirectiveSubmission(
            OrganizationId,
            $"{SubmissionBasePath}/{OrganizationId}/directives",
            request);
    }

    private static string AppendCompletionCriteria(string context) =>
        $"{context.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{CompletionCriteria}";
}
