using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Actors.Positions;

internal enum AiDirectiveProcessingStatus
{
    Received = 0,
    ContextAssembled = 1,
    GatewayRequested = 2,
    ResponseInterpreted = 3,
    ResultEmitted = 4,
    Failed = 5,
    Escalated = 6,
}

internal sealed record AiDirectiveProcessingTransition
{
    public AiDirectiveProcessingTransition(
        AiDirectiveProcessingStatus status,
        DateTimeOffset occurredAt,
        string? reason = null)
    {
        Status = status;
        OccurredAt = occurredAt;
        Reason = reason is null
            ? null
            : AiAgentGatewayText.Require(reason, nameof(reason));
    }

    public AiDirectiveProcessingStatus Status { get; }

    public DateTimeOffset OccurredAt { get; }

    public string? Reason { get; }
}

internal sealed record AiDirectiveProcessingSnapshot
{
    private AiDirectiveProcessingSnapshot(
        string correlationId,
        ThreadId threadId,
        DirectiveId directiveId,
        MessageId messageId,
        AiDirectiveProcessingStatus status,
        ImmutableArray<AiDirectiveProcessingTransition> history,
        string? terminalReason)
    {
        if (history.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Processing history must contain at least one transition.",
                nameof(history));
        }

        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        Status = status;
        History = history;
        TerminalReason = terminalReason is null
            ? null
            : AiAgentGatewayText.Require(terminalReason, nameof(terminalReason));
    }

    public string CorrelationId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public MessageId MessageId { get; }

    public AiDirectiveProcessingStatus Status { get; }

    public ImmutableArray<AiDirectiveProcessingTransition> History { get; }

    public string? TerminalReason { get; }

    public bool IsTerminal => IsTerminalStatus(Status);

    public static AiDirectiveProcessingSnapshot Received(
        AiDirectiveProcessingRequest request,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AiDirectiveProcessingSnapshot(
            request.CorrelationId,
            request.ThreadId,
            request.DirectiveId,
            request.MessageId,
            AiDirectiveProcessingStatus.Received,
            ImmutableArray.Create(new AiDirectiveProcessingTransition(
                AiDirectiveProcessingStatus.Received,
                occurredAt ?? DateTimeOffset.UtcNow)),
            terminalReason: null);
    }

    public AiDirectiveProcessingSnapshot AdvanceTo(
        AiDirectiveProcessingStatus nextStatus,
        DateTimeOffset? occurredAt = null,
        string? reason = null)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException(
                $"Cannot advance AI directive processing after terminal state '{Status}'.");
        }

        if (!CanAdvance(Status, nextStatus))
        {
            throw new InvalidOperationException(
                $"Cannot advance AI directive processing from '{Status}' to '{nextStatus}'.");
        }

        var terminalReason = TerminalReasonFor(nextStatus, reason);
        return new AiDirectiveProcessingSnapshot(
            CorrelationId,
            ThreadId,
            DirectiveId,
            MessageId,
            nextStatus,
            History.Add(new AiDirectiveProcessingTransition(
                nextStatus,
                occurredAt ?? DateTimeOffset.UtcNow,
                terminalReason ?? reason)),
            terminalReason);
    }

    private static bool CanAdvance(
        AiDirectiveProcessingStatus current,
        AiDirectiveProcessingStatus next) =>
        next switch
        {
            AiDirectiveProcessingStatus.ContextAssembled =>
                current == AiDirectiveProcessingStatus.Received,
            AiDirectiveProcessingStatus.GatewayRequested =>
                current == AiDirectiveProcessingStatus.ContextAssembled,
            AiDirectiveProcessingStatus.ResponseInterpreted =>
                current == AiDirectiveProcessingStatus.GatewayRequested,
            AiDirectiveProcessingStatus.ResultEmitted =>
                current == AiDirectiveProcessingStatus.ResponseInterpreted,
            AiDirectiveProcessingStatus.Failed => true,
            AiDirectiveProcessingStatus.Escalated => true,
            _ => false,
        };

    private static string? TerminalReasonFor(
        AiDirectiveProcessingStatus status,
        string? reason)
    {
        if (status is AiDirectiveProcessingStatus.Failed or AiDirectiveProcessingStatus.Escalated)
        {
            return AiAgentGatewayText.Require(
                reason ?? string.Empty,
                nameof(reason));
        }

        return null;
    }

    private static bool IsTerminalStatus(AiDirectiveProcessingStatus status) =>
        status is
            AiDirectiveProcessingStatus.ResultEmitted or
            AiDirectiveProcessingStatus.Failed or
            AiDirectiveProcessingStatus.Escalated;
}

internal sealed record GetAiDirectiveProcessingSnapshot
{
    public GetAiDirectiveProcessingSnapshot(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveProcessingSnapshotQueryResult
{
    private AiDirectiveProcessingSnapshotQueryResult(
        string correlationId,
        AiDirectiveProcessingSnapshot? snapshot)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Snapshot = snapshot;
    }

    public string CorrelationId { get; }

    public AiDirectiveProcessingSnapshot? Snapshot { get; }

    public bool Found => Snapshot is not null;

    public static AiDirectiveProcessingSnapshotQueryResult FoundSnapshot(
        AiDirectiveProcessingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AiDirectiveProcessingSnapshotQueryResult(
            snapshot.CorrelationId,
            snapshot);
    }

    public static AiDirectiveProcessingSnapshotQueryResult Missing(string correlationId) =>
        new(correlationId, snapshot: null);
}
