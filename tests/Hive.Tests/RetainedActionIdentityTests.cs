using System.Text.Json;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RetainedActionIdentityTests
{
    [Fact]
    public void Retained_action_id_is_non_empty_opaque_uuid()
    {
        var first = RetainedActionId.New();
        var second = RetainedActionId.New();

        Assert.NotEqual(first, second);
        Assert.Equal(first, RetainedActionId.From(first.Value));
        Assert.Equal(first.Value.ToString("D"), first.ToString());
        Assert.Throws<ArgumentException>(() => RetainedActionId.From(Guid.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void Action_fingerprint_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => ActionFingerprint.From(value));
    }

    [Fact]
    public void Action_fingerprint_round_trips_its_canonical_text()
    {
        var text = $"sha256:{new string('b', ActionFingerprint.DigestLength)}";

        var fingerprint = ActionFingerprint.From(text);

        Assert.Equal(text, fingerprint.ToString());
        Assert.Equal(fingerprint, ActionFingerprint.From(fingerprint.ToString()));
    }

    [Fact]
    public void Correlation_changes_identity_but_not_action_fingerprint()
    {
        var first = AiAgentRetainedActionFactory.Create(
            RetainedResult("directive:correlation-1"),
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)).Action;
        var repeated = AiAgentRetainedActionFactory.Create(
            RetainedResult("directive:correlation-1"),
            new DateTimeOffset(2026, 7, 12, 13, 0, 0, TimeSpan.Zero)).Action;
        var otherCorrelation = AiAgentRetainedActionFactory.Create(
            RetainedResult("directive:correlation-2"),
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero)).Action;

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.Fingerprint, repeated.Fingerprint);
        Assert.NotEqual(first.Id, otherCorrelation.Id);
        Assert.Equal(first.Fingerprint, otherCorrelation.Fingerprint);
    }

    [Fact]
    public void Tool_vector_pins_recursive_canonical_json_and_digest()
    {
        var candidate = ToolCandidate(
            "call-42",
            new Dictionary<string, object?>
            {
                ["path"] = "bugs/123.txt",
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["z"] = 2,
                    ["a"] = true,
                },
            });
        var facts = ToolFacts("external");

        var material = RetainedActionFingerprintFactory.Create(candidate, facts);

        Assert.Equal(
            "{\"facts\":{\"Action\":\"tool\",\"Attributes\":{\"path\":{\"Kind\":\"string\",\"Value\":\"bugs/123.txt\"},\"scope\":{\"Kind\":\"string\",\"Value\":\"external\"},\"tool\":{\"Kind\":\"string\",\"Value\":\"files\"}},\"SelectorValue\":\"files\"},\"kind\":\"tool\",\"payload\":{\"Arguments\":{\"metadata\":{\"a\":true,\"z\":2},\"path\":\"bugs/123.txt\"},\"Id\":\"call-42\",\"Name\":\"files\"},\"schemaVersion\":1,\"selector\":\"files\"}",
            material.CanonicalDocument);
        Assert.Equal(
            "sha256:faeefc6b4940e0adea68f97022a2ed961ae639f737c937a547b5c18a9e636bca",
            material.Fingerprint.Value);
    }

    [Fact]
    public void Message_vector_pins_canonical_json_and_digest()
    {
        var message = new Report(
            MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("agent")),
            new PositionEndpointRef(PositionId.From("lead")),
            ThreadId.From(Guid.Parse("20000000-0000-0000-0000-000000000002")),
            Priority.High,
            schemaVersion: 1,
            sentAt: new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero),
            deadline: null,
            DirectiveId.From(Guid.Parse("30000000-0000-0000-0000-000000000003")),
            ReportKind.Done,
            "Completed.");
        var candidate = AiAgentActionCandidate.ForMessage(
            message,
            ActingUnderDeclaration.Missing());
        var facts = ActionAttributeExtractorRunner.Extract(
            ActionDomainActionContract.ForOrganizationalMessage(nameof(Report)),
            registration: null,
            new ActionAttributeExtractionRequest(
                ActionDomainActionKind.OrganizationalMessage,
                nameof(Report))).Facts!;

        var material = RetainedActionFingerprintFactory.Create(candidate, facts);

        Assert.Equal(
            "{\"facts\":{\"Action\":\"organizational-message\",\"Attributes\":{\"message_type\":{\"Kind\":\"string\",\"Value\":\"Report\"}},\"SelectorValue\":\"Report\"},\"kind\":\"organizational-message\",\"payload\":{\"AboutDirectiveId\":\"30000000-0000-0000-0000-000000000003\",\"Body\":\"Completed.\",\"Deadline\":null,\"From\":{\"kind\":\"position\",\"positionId\":\"agent\"},\"Id\":\"10000000-0000-0000-0000-000000000001\",\"Kind\":\"done\",\"OrganizationId\":\"acme\",\"Priority\":\"high\",\"SchemaVersion\":1,\"SentAt\":\"2026-07-11T12:00:00\\u002B00:00\",\"Thread\":\"20000000-0000-0000-0000-000000000002\",\"To\":{\"kind\":\"position\",\"positionId\":\"lead\"}},\"schemaVersion\":1,\"selector\":\"Report\"}",
            material.CanonicalDocument);
        Assert.Equal(
            "sha256:725d1a8b4989b47b53ae572a0b450abe45af58485b37f3cc4c283c18e955a0a4",
            material.Fingerprint.Value);
    }

    [Fact]
    public void Equivalent_property_order_is_stable_and_functional_changes_are_distinct()
    {
        var first = RetainedActionFingerprintFactory.Create(
            ToolCandidate(
                "call-42",
                new Dictionary<string, object?>
                {
                    ["path"] = "bugs/123.txt",
                    ["metadata"] = new Dictionary<string, object?> { ["z"] = 2, ["a"] = true },
                }),
            ToolFacts("external"));
        var reordered = RetainedActionFingerprintFactory.Create(
            ToolCandidate(
                "call-42",
                new Dictionary<string, object?>
                {
                    ["metadata"] = new Dictionary<string, object?> { ["a"] = true, ["z"] = 2 },
                    ["path"] = "bugs/123.txt",
                }),
            ToolFacts("external"));
        var changedId = RetainedActionFingerprintFactory.Create(
            ToolCandidate(
                "call-43",
                new Dictionary<string, object?>
                {
                    ["metadata"] = new Dictionary<string, object?> { ["a"] = true, ["z"] = 2 },
                    ["path"] = "bugs/123.txt",
                }),
            ToolFacts("external"));
        var changedDerivedAttribute = RetainedActionFingerprintFactory.Create(
            ToolCandidate(
                "call-42",
                new Dictionary<string, object?>
                {
                    ["metadata"] = new Dictionary<string, object?> { ["a"] = true, ["z"] = 2 },
                    ["path"] = "bugs/123.txt",
                }),
            ToolFacts("internal"));

        Assert.Equal(first.CanonicalDocument, reordered.CanonicalDocument);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changedId.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changedDerivedAttribute.Fingerprint);
    }

    [Fact]
    public void Ambiguous_json_fails_before_a_partial_fingerprint_is_created()
    {
        using var document = JsonDocument.Parse("{\"duplicate\":1,\"duplicate\":2}");
        var candidate = ToolCandidate(
            "call-42",
            new Dictionary<string, object?>
            {
                ["path"] = "bugs/123.txt",
                ["ambiguous"] = document.RootElement.Clone(),
            });

        Assert.Throws<JsonException>(() =>
            RetainedActionFingerprintFactory.Create(candidate, ToolFacts("external")));
    }

    private static AiAgentActionCandidate ToolCandidate(
        string id,
        IReadOnlyDictionary<string, object?> arguments) =>
        AiAgentActionCandidate.ForTool(
            new AiToolCall(id, "files", arguments),
            ActingUnderDeclaration.Missing());

    private static AiAgentActionGateResult RetainedResult(string correlationId)
    {
        var organization = OrganizationId.From("acme");
        var position = PositionId.From("agent");
        var thread = ThreadId.From(Guid.Parse("40000000-0000-0000-0000-000000000004"));
        var candidate = ToolCandidate(
            "call-42",
            new Dictionary<string, object?> { ["path"] = "bugs/123.txt" });
        var governanceMessage = new ApprovalRequest(
            MessageId.From(Guid.Parse("50000000-0000-0000-0000-000000000005")),
            organization,
            new PositionEndpointRef(position),
            new OrganizationOwnerEndpointRef(),
            thread,
            Priority.High,
            1,
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero),
            null,
            "files",
            "Approval required.",
            ApprovalPolicyRef.From("policy/files"));
        var retention = new AiAgentActionRetentionIntent(
            candidate,
            correlationId,
            organization,
            position,
            thread,
            MessageId.From(Guid.Parse("60000000-0000-0000-0000-000000000006")),
            DirectiveId.From(Guid.Parse("70000000-0000-0000-0000-000000000007")),
            null,
            "action-gate-escalation-required",
            [governanceMessage]);

        return AiAgentActionGateResult.Retained(
            AiAgentActionGateOutcome.RetainedForEscalation,
            candidate,
            facts: null,
            resolution: null,
            "action-gate-escalation-required",
            retention);
    }

    private static ActionFacts ToolFacts(string scope)
    {
        var contract = ActionDomainActionContract.ForTool(
            "files",
            [
                ActionAttributeDefinition.Direct("path", ActionAttributeValueKind.String),
                ActionAttributeDefinition.Derived("scope", ActionAttributeValueKind.String),
            ]);
        var result = ActionAttributeExtractorRunner.Extract(
            contract,
            ActionAttributeExtractorRegistration.ForTool(
                "files",
                new ScopeExtractor(scope)),
            new ActionAttributeExtractionRequest(
                ActionDomainActionKind.Tool,
                "files",
                new Dictionary<string, ActionAttributeValue>
                {
                    ["path"] = ActionAttributeValue.FromString("bugs/123.txt"),
                }));

        return Assert.IsType<ActionFacts>(result.Facts);
    }

    private sealed class ScopeExtractor(string scope) : IActionAttributeExtractor
    {
        public ActionAttributeExtractorOutput Extract(ActionAttributeExtractionRequest request) =>
            ActionAttributeExtractorOutput.Success(
                new Dictionary<string, ActionAttributeValue>
                {
                    ["scope"] = ActionAttributeValue.FromString(scope),
                });
    }
}
