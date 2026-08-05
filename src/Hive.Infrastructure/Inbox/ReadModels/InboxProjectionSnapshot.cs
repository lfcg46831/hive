using Hive.Domain.Identity;

namespace Hive.Infrastructure.Inbox.ReadModels;

public sealed record InboxProjectionSnapshot
{
    public InboxProjectionSnapshot(
        OrganizationId organizationId,
        DateTimeOffset? lastEventAppliedAtUtc,
        IReadOnlyList<InboxProjectionItem> items)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        if (lastEventAppliedAtUtc is { } timestamp &&
            (timestamp == default || timestamp.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException(
                "Last applied event timestamp must be specified with the UTC offset.",
                nameof(lastEventAppliedAtUtc));
        }

        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(item => item.Key.OrganizationId != organizationId))
        {
            throw new ArgumentException(
                "Every inbox item must belong to the snapshot organization.",
                nameof(items));
        }

        LastEventAppliedAtUtc = lastEventAppliedAtUtc;
        Items = items;
    }

    public OrganizationId OrganizationId { get; }

    public DateTimeOffset? LastEventAppliedAtUtc { get; }

    public IReadOnlyList<InboxProjectionItem> Items { get; }
}
