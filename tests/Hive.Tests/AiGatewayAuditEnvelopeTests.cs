using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class AiGatewayAuditEnvelopeTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset StartedAt =
        new(2026, 6, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddMilliseconds(25);

    [Fact]
    public void Constructor_rejects_ambiguous_payload_and_missing_rejection_reason()
    {
        var request = new AiGatewayAuditRequestSnapshot("Classify this bug.");
        var response = new AiGatewayAuditResponseSnapshot(
            "The bug is reproducible.",
            AiFinishReason.Stop);
        var error = new AiGatewayAuditErrorSnapshot(
            AiGatewayErrorCode.Timeout,
            "The provider timed out.",
            isRetryable: true);

        Assert.Throws<ArgumentException>(() => new AiGatewayAuditEnvelope(
            Organization,
            Position,
            Thread,
            Message,
            StartedAt,
            CompletedAt,
            AiGatewayCallResult.Succeeded,
            request));

        Assert.Throws<ArgumentException>(() => new AiGatewayAuditEnvelope(
            Organization,
            Position,
            Thread,
            Message,
            StartedAt,
            CompletedAt,
            AiGatewayCallResult.Succeeded,
            request,
            response: response,
            rejectionReason: "timeout"));

        Assert.Throws<ArgumentException>(() => new AiGatewayAuditEnvelope(
            Organization,
            Position,
            Thread,
            Message,
            StartedAt,
            CompletedAt,
            AiGatewayCallResult.Failed,
            request,
            error: error));
    }

    [Fact]
    public void Redactions_are_snapshotted_and_reject_empty_path_or_reason()
    {
        var redactions = new List<AiGatewayAuditRedaction>
        {
            new("request.content", "email"),
        };
        var envelope = new AiGatewayAuditEnvelope(
            Organization,
            Position,
            Thread,
            Message,
            StartedAt,
            CompletedAt,
            AiGatewayCallResult.Succeeded,
            new AiGatewayAuditRequestSnapshot("Classify this bug."),
            response: new AiGatewayAuditResponseSnapshot(
                "The bug is reproducible.",
                AiFinishReason.Stop),
            redactions: redactions);

        redactions.Clear();

        var redaction = Assert.Single(envelope.Redactions);
        Assert.Equal("request.content", redaction.Path);
        Assert.Equal("email", redaction.Reason);
        Assert.Throws<ArgumentException>(() => new AiGatewayAuditRedaction("", "email"));
        Assert.Throws<ArgumentException>(() => new AiGatewayAuditRedaction("request.content", ""));
    }
}
