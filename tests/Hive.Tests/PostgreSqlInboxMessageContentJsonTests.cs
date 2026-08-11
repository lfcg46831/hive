using Hive.Domain.Messaging;
using Hive.Infrastructure.Inbox.ReadModels;
using Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

namespace Hive.Tests;

public sealed class PostgreSqlInboxMessageContentJsonTests
{
    public static TheoryData<InboxProjectionMessageType, InboxProjectionMessageContent> Content
    {
        get
        {
            var data = new TheoryData<
                InboxProjectionMessageType,
                InboxProjectionMessageContent>();
            data.Add(
                InboxProjectionMessageType.Directive,
                new InboxProjectionDirectiveContent("Objective", "Context"));
            data.Add(
                InboxProjectionMessageType.Report,
                new InboxProjectionReportContent("Body", ReportKind.Done));
            data.Add(
                InboxProjectionMessageType.Escalation,
                new InboxProjectionEscalationContent("Issue", "Context"));
            data.Add(
                InboxProjectionMessageType.Memo,
                new InboxProjectionMemoContent("Body"));
            data.Add(
                InboxProjectionMessageType.PeerRequest,
                new InboxProjectionPeerRequestContent("Ask"));
            data.Add(
                InboxProjectionMessageType.PeerResponse,
                new InboxProjectionPeerResponseContent("Body"));
            data.Add(
                InboxProjectionMessageType.ApprovalRequest,
                new InboxProjectionApprovalRequestContent("Action", "Justification"));
            data.Add(
                InboxProjectionMessageType.ApprovalDecision,
                new InboxProjectionApprovalDecisionContent("Reason"));
            data.Add(
                InboxProjectionMessageType.ApprovalDecision,
                new InboxProjectionApprovalDecisionContent(Reason: null));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Content))]
    public void Round_trips_each_closed_projection_content_shape(
        InboxProjectionMessageType messageType,
        InboxProjectionMessageContent content)
    {
        var json = PostgreSqlInboxMessageContentJson.Serialize(messageType, content);

        var restored = PostgreSqlInboxMessageContentJson.Deserialize(messageType, json);

        Assert.Equal(content, restored);
    }

    [Fact]
    public void Rejects_type_mismatches_and_open_json_shapes()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlInboxMessageContentJson.Serialize(
                InboxProjectionMessageType.Directive,
                new InboxProjectionMemoContent("Body")));
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlInboxMessageContentJson.Deserialize(
                InboxProjectionMessageType.Memo,
                "{\"body\":\"Body\",\"unexpected\":true}"));
    }
}
