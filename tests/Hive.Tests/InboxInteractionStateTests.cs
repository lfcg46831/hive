using Hive.Domain.Identity;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Tests;

public sealed class InboxInteractionStateTests
{
    private static readonly InboxProjectionItemKey ItemKey = new(
        OrganizationId.From("acme"),
        PositionId.From("delivery-lead"),
        MessageId.From(Guid.Parse("8f308049-e1ce-4a62-b8f2-d44a15268d9d")));

    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contracts_exclude_derived_state_and_validate_draft_ownership()
    {
        Assert.Throws<ArgumentException>(() => new InboxInteractionState(
            ItemKey,
            "person-alice",
            InboxInteractionReadState.Unread,
            InboxInteractionReplyState.NotStarted,
            "Draft outside a reply",
            OccurredAt));
        Assert.Throws<ArgumentException>(() => new InboxInteractionMutation(
            ItemKey,
            "person-alice",
            InboxInteractionAction.SaveDraft,
            OccurredAt));
        Assert.Throws<ArgumentException>(() => new InboxInteractionMutation(
            ItemKey,
            "person-alice",
            InboxInteractionAction.MarkRead,
            OccurredAt,
            "Unexpected draft"));
    }
}
