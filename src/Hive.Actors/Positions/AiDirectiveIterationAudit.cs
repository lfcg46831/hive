using System.Collections.Immutable;
using Hive.Domain.Governance;

namespace Hive.Actors.Positions;

internal enum AiDirectiveIterationAuditEventKind
{
    Decision = 1,
    Execution = 2,
}

internal sealed record AiDirectiveIterationAuditEntry
{
    public AiDirectiveIterationAuditEntry(
        string correlationId,
        int iteration,
        DateTimeOffset observedAt,
        AiDirectiveIterationAuditEventKind kind,
        string code,
        string auditReason,
        AiDirectiveIterationContinuationKind? continuationKind = null,
        AiDirectiveIterationExecutionKind? executionKind = null,
        ActingUnderDeclaration? actingUnder = null)
    {
        if (iteration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iteration),
                iteration,
                "AI directive iteration audit entry iteration must be greater than zero.");
        }

        if (observedAt == default)
        {
            throw new ArgumentException(
                "AI directive iteration audit observation time must be specified.",
                nameof(observedAt));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unknown AI directive iteration audit event kind.");
        }

        if (continuationKind is { } continuation && !Enum.IsDefined(continuation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationKind),
                continuationKind,
                "Unknown AI directive iteration continuation kind.");
        }

        if (executionKind is { } execution && !Enum.IsDefined(execution))
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionKind),
                executionKind,
                "Unknown AI directive iteration execution kind.");
        }

        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Iteration = iteration;
        ObservedAt = observedAt;
        Kind = kind;
        Code = AiAgentGatewayText.Require(code, nameof(code));
        AuditReason = AiAgentGatewayText.Require(auditReason, nameof(auditReason));
        ContinuationKind = continuationKind;
        ExecutionKind = executionKind;
        ActingUnderState = actingUnder?.State;
        ActingUnderCode = actingUnder?.Code;
        ActingUnderKey = actingUnder?.State == ActingUnderDeclarationState.Declared
            ? actingUnder.Key
            : null;
    }

    public string CorrelationId { get; }

    public int Iteration { get; }

    public DateTimeOffset ObservedAt { get; }

    public AiDirectiveIterationAuditEventKind Kind { get; }

    public string Code { get; }

    public string AuditReason { get; }

    public AiDirectiveIterationContinuationKind? ContinuationKind { get; }

    public AiDirectiveIterationExecutionKind? ExecutionKind { get; }

    public ActingUnderDeclarationState? ActingUnderState { get; }

    public string? ActingUnderCode { get; }

    public AuthorityKey? ActingUnderKey { get; }
}

internal sealed record AiDirectiveIterationAuditTrail
{
    private AiDirectiveIterationAuditTrail(
        string correlationId,
        ImmutableArray<AiDirectiveIterationAuditEntry> entries,
        AiDirectiveIterationStopKind? terminalStopKind,
        string? terminalCode,
        string? terminalAuditReason)
    {
        if (entries.IsDefault)
        {
            throw new ArgumentException(
                "AI directive iteration audit entries cannot be default.",
                nameof(entries));
        }

        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Entries = RequireEntries(correlationId, entries);
        TerminalStopKind = terminalStopKind;
        TerminalCode = terminalCode is null
            ? null
            : AiAgentGatewayText.Require(terminalCode, nameof(terminalCode));
        TerminalAuditReason = terminalAuditReason is null
            ? null
            : AiAgentGatewayText.Require(terminalAuditReason, nameof(terminalAuditReason));
    }

    public string CorrelationId { get; }

    public ImmutableArray<AiDirectiveIterationAuditEntry> Entries { get; }

    public AiDirectiveIterationStopKind? TerminalStopKind { get; }

    public string? TerminalCode { get; }

    public string? TerminalAuditReason { get; }

    public bool IsTerminal => TerminalCode is not null;

    public static AiDirectiveIterationAuditTrail Start(AiDirectiveIterationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new AiDirectiveIterationAuditTrail(
            state.CorrelationId,
            ImmutableArray<AiDirectiveIterationAuditEntry>.Empty,
            terminalStopKind: null,
            terminalCode: null,
            terminalAuditReason: null);
    }

    public AiDirectiveIterationAuditTrail RecordDecision(
        AiDirectiveIterationState state,
        AiDirectiveIterationDecision decision,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(decision);
        EnsureCanAppend(state.CorrelationId);

        var stopReason = decision.StopReason;
        var entry = new AiDirectiveIterationAuditEntry(
            state.CorrelationId,
            state.CurrentIteration,
            observedAt,
            AiDirectiveIterationAuditEventKind.Decision,
            stopReason?.Code ?? "continue",
            stopReason?.AuditReason ?? "AI directive loop selected a continuation.",
            ContinuationKind(decision),
            executionKind: null,
            actingUnder: ActingUnder(decision));

        return Append(
            entry,
            stopReason?.Kind,
            stopReason?.Code,
            stopReason?.AuditReason);
    }

    public AiDirectiveIterationAuditTrail RecordExecution(
        AiDirectiveIterationState state,
        AiDirectiveIterationExecutionResult result,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(result);
        EnsureCanAppend(state.CorrelationId);

        if (!string.Equals(
            state.CorrelationId,
            result.CorrelationId,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AI directive iteration execution result correlation must match the state correlation.",
                nameof(result));
        }

        var failure = result.Failure;
        var entry = new AiDirectiveIterationAuditEntry(
            state.CorrelationId,
            state.CurrentIteration,
            observedAt,
            AiDirectiveIterationAuditEventKind.Execution,
            failure?.Code ?? SuccessCode(result.Kind),
            failure?.AuditReason ?? SuccessAuditReason(result.Kind),
            continuationKind: null,
            result.Kind);

        return Append(
            entry,
            terminalStopKind: null,
            failure?.Code,
            failure?.AuditReason);
    }

    private AiDirectiveIterationAuditTrail Append(
        AiDirectiveIterationAuditEntry entry,
        AiDirectiveIterationStopKind? terminalStopKind,
        string? terminalCode,
        string? terminalAuditReason) =>
        new(
            CorrelationId,
            Entries.Add(entry),
            terminalStopKind,
            terminalCode,
            terminalAuditReason);

    private void EnsureCanAppend(string correlationId)
    {
        if (!string.Equals(CorrelationId, correlationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AI directive iteration audit correlation must match the state correlation.",
                nameof(correlationId));
        }

        if (IsTerminal)
        {
            throw new InvalidOperationException(
                "Cannot append AI directive iteration audit entries after a terminal decision.");
        }
    }

    private static AiDirectiveIterationContinuationKind? ContinuationKind(
        AiDirectiveIterationDecision decision) =>
        decision.CanContinue && decision.Continuations.Length == 1
            ? decision.Continuations[0].Kind
            : null;

    private static ActingUnderDeclaration? ActingUnder(
        AiDirectiveIterationDecision decision) =>
        decision.CanContinue &&
        decision.Continuations.Length == 1 &&
        decision.Continuations[0].Kind == AiDirectiveIterationContinuationKind.ConnectorTool
            ? decision.Continuations[0].ActingUnder
            : null;

    private static string SuccessCode(AiDirectiveIterationExecutionKind? kind) =>
        kind switch
        {
            AiDirectiveIterationExecutionKind.Inference => "inference-succeeded",
            AiDirectiveIterationExecutionKind.ConnectorTool => "connector-tool-succeeded",
            _ => "iteration-execution-succeeded",
        };

    private static string SuccessAuditReason(AiDirectiveIterationExecutionKind? kind) =>
        kind switch
        {
            AiDirectiveIterationExecutionKind.Inference =>
                "AI directive iteration inference completed successfully.",
            AiDirectiveIterationExecutionKind.ConnectorTool =>
                "AI directive iteration connector tool completed successfully.",
            _ => "AI directive iteration execution completed successfully.",
        };

    private static ImmutableArray<AiDirectiveIterationAuditEntry> RequireEntries(
        string correlationId,
        ImmutableArray<AiDirectiveIterationAuditEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException(
                    "AI directive iteration audit entries cannot contain null entries.",
                    nameof(entries));
            }

            if (!string.Equals(entry.CorrelationId, correlationId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "AI directive iteration audit entries must match the snapshot correlation.",
                    nameof(entries));
            }
        }

        return entries;
    }
}

internal sealed record GetAiDirectiveIterationAuditSnapshot
{
    public GetAiDirectiveIterationAuditSnapshot(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveIterationAuditSnapshotQueryResult
{
    private AiDirectiveIterationAuditSnapshotQueryResult(
        string correlationId,
        AiDirectiveIterationAuditTrail? snapshot)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
        Snapshot = snapshot;
    }

    public string CorrelationId { get; }

    public AiDirectiveIterationAuditTrail? Snapshot { get; }

    public bool Found => Snapshot is not null;

    public static AiDirectiveIterationAuditSnapshotQueryResult FoundSnapshot(
        AiDirectiveIterationAuditTrail snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AiDirectiveIterationAuditSnapshotQueryResult(
            snapshot.CorrelationId,
            snapshot);
    }

    public static AiDirectiveIterationAuditSnapshotQueryResult Missing(string correlationId) =>
        new(correlationId, snapshot: null);
}
