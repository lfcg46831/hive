using Hive.Domain.Identity;

namespace Hive.Infrastructure.Inbox.ReadModels;

public interface IInboxInteractionReader
{
    bool IsAvailable { get; }

    ValueTask<IReadOnlyDictionary<InboxProjectionItemKey, InboxInteractionState>> ReadAsync(
        OrganizationId organizationId,
        string personId,
        IReadOnlyCollection<InboxProjectionItemKey> itemKeys,
        CancellationToken cancellationToken = default);
}

public interface IInboxInteractionStore : IInboxInteractionReader
{
    ValueTask<InboxInteractionState> ApplyAsync(
        InboxInteractionMutation mutation,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<InboxInteractionAuditEntry>> ReadAuditAsync(
        InboxProjectionItemKey itemKey,
        string personId,
        CancellationToken cancellationToken = default);
}
