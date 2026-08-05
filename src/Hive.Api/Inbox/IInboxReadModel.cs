using Hive.Contracts.Inbox;
using Hive.Api.Authorization;
using Hive.Domain.Identity;

namespace Hive.Api.Inbox;

/// <summary>
/// Read-only seam between the public inbox API and the materialized, principal-scoped inbox view.
/// </summary>
public interface IInboxReadModel
{
    /// <summary>
    /// Lists the authenticated principal's items in the organization, optionally narrowed to one
    /// occupied position. Implementations apply the fixed order: earliest deadline first (missing
    /// deadlines last), highest priority first, newest message first, then item identifier ordinal.
    /// </summary>
    ValueTask<InboxReadResult<InboxPage>> ListAsync(
        PersonOrganizationScope scope,
        PositionId? positionId,
        InboxListQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one item only when it belongs to the authenticated principal's effective inbox scope.
    /// </summary>
    ValueTask<InboxReadResult<InboxItemResponse>> ReadItemAsync(
        PersonOrganizationScope scope,
        string itemId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Server-side filters and keyset pagination forwarded to the inbox read model.
/// </summary>
public sealed record InboxListQuery
{
    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 100;

    public InboxListQuery(
        InboxMessageType? messageType = null,
        InboxReadState? readState = null,
        InboxResponseState? responseState = null,
        InboxPriority? priority = null,
        DateTimeOffset? deadlineFromUtc = null,
        DateTimeOffset? deadlineToUtc = null,
        bool? approvalPending = null,
        int pageSize = DefaultPageSize,
        string? cursor = null)
    {
        MessageType = OptionalDefinedEnum(messageType, nameof(messageType));
        ReadState = OptionalDefinedEnum(readState, nameof(readState));
        ResponseState = OptionalDefinedEnum(responseState, nameof(responseState));
        Priority = OptionalDefinedEnum(priority, nameof(priority));
        DeadlineFromUtc = OptionalUtcTimestamp(deadlineFromUtc, nameof(deadlineFromUtc));
        DeadlineToUtc = OptionalUtcTimestamp(deadlineToUtc, nameof(deadlineToUtc));
        ApprovalPending = approvalPending;

        if (DeadlineFromUtc > DeadlineToUtc)
        {
            throw new ArgumentException(
                "The deadline lower bound cannot follow the upper bound.",
                nameof(deadlineFromUtc));
        }

        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        PageSize = pageSize;
        Cursor = ValidateCursor(cursor);
    }

    public InboxMessageType? MessageType { get; }

    public InboxReadState? ReadState { get; }

    public InboxResponseState? ResponseState { get; }

    public InboxPriority? Priority { get; }

    public DateTimeOffset? DeadlineFromUtc { get; }

    public DateTimeOffset? DeadlineToUtc { get; }

    public bool? ApprovalPending { get; }

    public int PageSize { get; }

    public string? Cursor { get; }

    private static T? OptionalDefinedEnum<T>(T? value, string parameterName)
        where T : struct, Enum
    {
        if (value is not null && !Enum.IsDefined(value.Value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value is not defined.");
        }

        return value;
    }

    private static DateTimeOffset? OptionalUtcTimestamp(
        DateTimeOffset? value,
        string parameterName)
    {
        if (value is not null && value.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }

        return value;
    }

    private static string? ValidateCursor(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Cursor cannot be empty or contain leading or trailing whitespace.",
                nameof(value));
        }

        if (value.Length > 2_048)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.Length,
                "Cursor cannot exceed 2048 characters.");
        }

        return value;
    }
}

/// <summary>
/// Distinguishes a missing inbox resource from a read model unavailable on this node.
/// </summary>
public readonly record struct InboxReadResult<T>
    where T : class
{
    private InboxReadResult(bool isAvailable, T? value)
    {
        IsAvailable = isAvailable;
        Value = value;
    }

    public bool IsAvailable { get; }

    public T? Value { get; }

    public static InboxReadResult<T> Available(T? value) => new(true, value);

    public static InboxReadResult<T> Unavailable { get; } = new(false, null);
}

internal sealed class UnavailableInboxReadModel : IInboxReadModel
{
    public static UnavailableInboxReadModel Instance { get; } = new();

    private UnavailableInboxReadModel()
    {
    }

    public ValueTask<InboxReadResult<InboxPage>> ListAsync(
        PersonOrganizationScope scope,
        PositionId? positionId,
        InboxListQuery query,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InboxReadResult<InboxPage>.Unavailable);

    public ValueTask<InboxReadResult<InboxItemResponse>> ReadItemAsync(
        PersonOrganizationScope scope,
        string itemId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(InboxReadResult<InboxItemResponse>.Unavailable);
}
